using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace ChaoDesk.Host.Panel;

/// <summary>
/// Keeps the full screen sample and its blur, but bounds the expensive lens pass
/// to the panel and the pixels its refraction can read. No bitmap cache or
/// explicit shader brush resamples the capture to the output dimensions.
/// </summary>
internal sealed class GlassScene : Canvas
{
    private readonly GlassLensEffect _lens;
    private readonly Image _source = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly BlurEffect _blur = new() { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };
    private readonly Grid _opticalLayer = new();
    private readonly TranslateTransform _layerOffset = new();
    private readonly TranslateTransform _sourceOffset = new();
    private DpiScale _dpi = new(1, 1);
    private Rect _captureBounds = Rect.Empty;
    private Rect _paneBounds = Rect.Empty;
    private double _guardX, _guardY;

    internal Rect LensBounds { get; private set; } = Rect.Empty;
    // Requested screen mapping; SourceOffset may temporarily clamp its display
    // position when a large jump outruns the current capture.
    internal Point4D RawCaptureMapping { get; private set; } = new(0, 0, 1, 1);
    internal Point SourceOffset => new(_sourceOffset.X, _sourceOffset.Y);
    internal BitmapSource? Source => _source.Source as BitmapSource;

    internal GlassScene(GlassLensEffect lens)
    {
        _lens = lens;
        IsHitTestVisible = false;
        ClipToBounds = true;
        UseLayoutRounding = false;
        SnapsToDevicePixels = false;
        _source.Effect = _blur;
        _source.RenderTransform = _sourceOffset;
        RenderOptions.SetBitmapScalingMode(_source, BitmapScalingMode.Linear);
        var sourceCanvas = new Canvas { IsHitTestVisible = false };
        sourceCanvas.Children.Add(_source);
        // Clip after the blur. The full capture still supplies the Gaussian
        // kernel at this boundary; only its finished output is cropped.
        var viewport = new Border { ClipToBounds = true, Background = Brushes.Transparent, Child = sourceCanvas };
        _opticalLayer.Children.Add(viewport);
        _opticalLayer.Effect = lens;
        _opticalLayer.RenderTransform = _layerOffset;
        Children.Add(_opticalLayer);
    }

    internal void Configure(Size panelSize, DpiScale dpi, double blurRadius, double displacement, double aberration)
    {
        _dpi = dpi;
        _blur.Radius = blurRadius;
        // All channel displacement scales fit within this bound. Two physical
        // pixels preserve bilinear sampling at the lens input boundary.
        double reach = Math.Max(0, displacement) * .5 * (1 + Math.Max(0, aberration) * .1);
        _guardX = Math.Ceiling(reach * dpi.DpiScaleX + 2) / dpi.DpiScaleX;
        _guardY = Math.Ceiling(reach * dpi.DpiScaleY + 2) / dpi.DpiScaleY;
        // The offscreen input bounds must also end on physical pixels when a
        // page's measured size has a fractional DIP component.
        double width = Math.Ceiling(Math.Max(1, panelSize.Width) * dpi.DpiScaleX) / dpi.DpiScaleX + 2 * _guardX;
        double height = Math.Ceiling(Math.Max(1, panelSize.Height) * dpi.DpiScaleY) / dpi.DpiScaleY + 2 * _guardY;
        _opticalLayer.Width = width;
        _opticalLayer.Height = height;
        _layerOffset.X = -_guardX;
        _layerOffset.Y = -_guardY;
        LensBounds = new Rect(-_guardX, -_guardY, width, height);
        _lens.Capture = new Point4D(_guardX / width, _guardY / height,
            Math.Max(1, panelSize.Width) / width, Math.Max(1, panelSize.Height) / height);
        UpdateSourceSize();
        UpdateMapping(_paneBounds);
    }

    internal void SetFrame(BitmapSource source, Rect captureBounds, Rect paneScreenBounds)
    {
        _source.Source = source;
        _captureBounds = captureBounds;
        UpdateSourceSize();
        UpdateMapping(paneScreenBounds);
    }

    private void UpdateSourceSize()
    {
        if (_captureBounds.IsEmpty) return;
        _source.Width = _captureBounds.Width / _dpi.DpiScaleX;
        _source.Height = _captureBounds.Height / _dpi.DpiScaleY;
    }

    internal void UpdateMapping(Rect paneScreenBounds)
    {
        _paneBounds = paneScreenBounds;
        if (_captureBounds.IsEmpty || paneScreenBounds.IsEmpty) return;
        RawCaptureMapping = GlassCaptureMapping.Create(_captureBounds, paneScreenBounds);
        // Moving the panel changes only this composition transform. It does not
        // resize the capture, move the lens, or change the lens coordinate field.
        double requestedX = (_captureBounds.Left - paneScreenBounds.Left) / _dpi.DpiScaleX + _guardX;
        double requestedY = (_captureBounds.Top - paneScreenBounds.Top) / _dpi.DpiScaleY + _guardY;
        // A jump beyond the entire capture must not leave the lens input
        // transparent. Retain one physical texel in each axis so SceneBounds
        // extends captured edge pixels until the new screen region arrives.
        // This is a display fallback; RawCaptureMapping keeps the real request.
        _sourceOffset.X = Math.Clamp(requestedX, -_source.Width + 1 / _dpi.DpiScaleX, LensBounds.Width - 1 / _dpi.DpiScaleX);
        _sourceOffset.Y = Math.Clamp(requestedY, -_source.Height + 1 / _dpi.DpiScaleY, LensBounds.Height - 1 / _dpi.DpiScaleY);
        // At a desktop edge the screen capture cannot supply the offscreen
        // part of the optical guard. Clamp refraction to available pixel
        // centers instead of sampling that transparent gap.
        double minU = Math.Clamp((_sourceOffset.X + .5 / _dpi.DpiScaleX) / LensBounds.Width, 0, 1);
        double minV = Math.Clamp((_sourceOffset.Y + .5 / _dpi.DpiScaleY) / LensBounds.Height, 0, 1);
        double maxU = Math.Clamp((_sourceOffset.X + _source.Width - .5 / _dpi.DpiScaleX) / LensBounds.Width, minU, 1);
        double maxV = Math.Clamp((_sourceOffset.Y + _source.Height - .5 / _dpi.DpiScaleY) / LensBounds.Height, minV, 1);
        _lens.SceneBounds = new Point4D(minU, minV, maxU, maxV);
    }

    internal void Clear()
    {
        _source.Source = null;
        _captureBounds = _paneBounds = Rect.Empty;
        RawCaptureMapping = new Point4D(0, 0, 1, 1);
        _lens.SceneBounds = new Point4D(0, 0, 1, 1);
    }
}
