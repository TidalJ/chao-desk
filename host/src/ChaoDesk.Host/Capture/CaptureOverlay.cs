using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ChaoDesk.Host.Desktop;
using ChaoDesk.Host.Localization;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Capture;

public enum CaptureAction { Cancel, Copy, Save, Pin, Scroll, Ocr }

/// <summary>What a capture is for: marking up and copying, pinning the chosen area, or scrolling (each has its hotkey).</summary>
public enum CaptureMode { Copy, Pin, Scroll, Ocr }

/// <summary>
/// The frozen-screen overlay for one monitor (Snipaste style): hover highlights the window or panel under the
/// mouse (click takes it), drag selects a region, handles resize it, the toolbar annotates and finishes. All
/// geometry is kept in physical screen pixels; the window shows them 1:1 by laying out in DIPs = pixels ÷ scale.
/// </summary>
public sealed class CaptureOverlay : Window
{
    private enum Mode { Hover, Creating, Selected, Moving, Resizing, Drawing }

    private readonly ScreenPixels _shot;
    private readonly IReadOnlyList<SnapRect> _targets;
    private readonly MonitorInfo _monitor;
    private readonly double _scale;
    private readonly Action<CaptureOverlay, CaptureAction> _finish;
    private readonly CaptureMode _captureMode;

    private readonly Canvas _root = new();
    private readonly System.Windows.Shapes.Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x78, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _frame = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x5B, 0x6A, 0xF0)), StrokeThickness = 1.5, IsHitTestVisible = false };
    private readonly Rectangle[] _handles = new Rectangle[8];
    private readonly AnnotationLayer _layer;
    private readonly Border _magnifier = new();
    private readonly Image _magImage = new() { Width = 132, Height = 132, Stretch = Stretch.Fill };
    private readonly TextBlock _magText = new() { Foreground = Brushes.White, FontSize = 11.5, Margin = new Thickness(6, 3, 6, 4) };
    private readonly Border _sizeLabel = new();
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 11.5 };
    private readonly Border _toolbar = new();
    private readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _palette = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 4), Visibility = Visibility.Collapsed };
    private readonly Dictionary<AnnotationTool, Border> _toolButtons = new();
    private TextBox? _textEditor;

    private Mode _mode = Mode.Hover;
    private PixelRect? _hover;
    private PixelRect _selection;
    private bool _hasSelection;
    private Point _dragStart;           // physical
    private PixelRect _dragOrigin;
    private int _resizeHandle;
    private AnnotationTool _tool = AnnotationTool.None;
    private Color _color = AnnotationRenderer.Palette[0];
    private double _width = AnnotationRenderer.Widths[1];
    private Annotation? _drawing;
    private int _cursorX, _cursorY;

    public List<Annotation> Annotations { get; } = new();
    public PixelRect Selection => _selection;
    public bool HasSelection => _hasSelection;

    public CaptureOverlay(ScreenPixels shot, IReadOnlyList<SnapRect> targets, MonitorInfo monitor, CaptureMode mode, Action<CaptureOverlay, CaptureAction> finish)
    {
        _shot = shot;
        _targets = targets;
        _monitor = monitor;
        _scale = monitor.Scale;
        _finish = finish;
        _captureMode = mode;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        UseLayoutRounding = true;
        Cursor = Cursors.Cross;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        Width = monitor.Bounds.Width / _scale;
        Height = monitor.Bounds.Height / _scale;

        var background = new Image
        {
            Source = shot.Crop(monitor.Bounds),
            Width = Width,
            Height = Height,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(background, BitmapScalingMode.NearestNeighbor);
        _root.Children.Add(background);
        _root.Children.Add(_dim);
        _layer = new AnnotationLayer(this) { Width = Width, Height = Height, IsHitTestVisible = false };
        _root.Children.Add(_layer);
        _root.Children.Add(_frame);
        for (int i = 0; i < 8; i++)
        {
            _handles[i] = new Rectangle { Width = 7, Height = 7, Fill = Brushes.White, Stroke = _frame.Stroke, StrokeThickness = 1, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            _root.Children.Add(_handles[i]);
        }
        _sizeLabel.Child = _sizeText;
        _sizeLabel.Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x1F, 0x23, 0x28));
        _sizeLabel.CornerRadius = new CornerRadius(4);
        _sizeLabel.Padding = new Thickness(6, 2, 6, 3);
        _sizeLabel.IsHitTestVisible = false;
        _root.Children.Add(_sizeLabel);
        BuildMagnifier();
        BuildToolbar();
        Content = _root;

        SourceInitialized += (_, _) => PlaceOnMonitor();
        DpiChanged += (_, _) => PlaceOnMonitor();
        MouseMove += OnMouseMove;
        MouseLeftButtonDown += OnMouseDown;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonUp += OnRightClick;
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => { Activate(); Focus(); UpdateVisuals(); };
    }

    private void PlaceOnMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var b = _monitor.Bounds;
        SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, b.X, b.Y, b.Width, b.Height, SWP_SHOWWINDOW);
    }

    // ---- Coordinates -------------------------------------------------------------------------------

    private Point ToPhysical(Point dip) => new(_monitor.Bounds.X + dip.X * _scale, _monitor.Bounds.Y + dip.Y * _scale);
    private Rect ToDip(PixelRect r) => new((r.X - _monitor.Bounds.X) / _scale, (r.Y - _monitor.Bounds.Y) / _scale, r.Width / _scale, r.Height / _scale);
    private PixelRect ClampToMonitor(PixelRect r) => ScreenPixels.Intersect(r, _monitor.Bounds);

    private static PixelRect FromPoints(Point a, Point b)
    {
        int x0 = (int)Math.Round(Math.Min(a.X, b.X)), y0 = (int)Math.Round(Math.Min(a.Y, b.Y));
        int x1 = (int)Math.Round(Math.Max(a.X, b.X)), y1 = (int)Math.Round(Math.Max(a.Y, b.Y));
        return new PixelRect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    /// <summary>A point relative to the selection, in physical pixels (annotation space).</summary>
    private Point ToSelection(Point physical) => new(Math.Clamp(physical.X - _selection.X, 0, _selection.Width), Math.Clamp(physical.Y - _selection.Y, 0, _selection.Height));

    // ---- Mouse ---------------------------------------------------------------------------------------

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = ToPhysical(e.GetPosition(_root));
        _cursorX = (int)p.X;
        _cursorY = (int)p.Y;
        switch (_mode)
        {
            case Mode.Hover:
                _hover = ScreenCapture.SnapAt(_targets, _cursorX, _cursorY) is { } snap ? ClampToMonitor(snap) : _monitor.Bounds;
                break;
            case Mode.Creating:
                if (Math.Abs(p.X - _dragStart.X) > 3 || Math.Abs(p.Y - _dragStart.Y) > 3)
                {
                    _selection = ClampToMonitor(FromPoints(_dragStart, p));
                    _hasSelection = true;
                }
                break;
            case Mode.Moving:
            {
                int dx = (int)(p.X - _dragStart.X), dy = (int)(p.Y - _dragStart.Y);
                var b = _monitor.Bounds;
                int x = Math.Clamp(_dragOrigin.X + dx, b.X, b.Right - _dragOrigin.Width);
                int y = Math.Clamp(_dragOrigin.Y + dy, b.Y, b.Bottom - _dragOrigin.Height);
                _selection = _dragOrigin with { X = x, Y = y };
                break;
            }
            case Mode.Resizing:
                _selection = ClampToMonitor(Resize(_dragOrigin, _resizeHandle, p));
                break;
            case Mode.Drawing when _drawing != null:
                var q = ToSelection(p);
                if (_drawing.Tool is AnnotationTool.Pen or AnnotationTool.Marker) _drawing.Points.Add(q);
                else if (_drawing.Points.Count == 1) _drawing.Points.Add(q);
                else _drawing.Points[^1] = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Constrain(_drawing.Points[0], q, _drawing.Tool) : q;
                _layer.InvalidateVisual();
                break;
        }
        if (_mode == Mode.Selected) Cursor = CursorFor(p);
        UpdateVisuals();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = ToPhysical(e.GetPosition(_root));
        CommitText();
        if (e.ClickCount == 2 && _hasSelection && _selection.Contains((int)p.X, (int)p.Y) && _tool == AnnotationTool.None)
        {
            Finish(CaptureAction.Copy);
            return;
        }
        if (_mode == Mode.Selected)
        {
            bool inside = _selection.Contains((int)p.X, (int)p.Y);
            if (_tool != AnnotationTool.None && inside)
            {
                StartAnnotation(p);
                return;
            }
            int handle = HandleAt(p);
            if (handle >= 0) { _mode = Mode.Resizing; _resizeHandle = handle; _dragOrigin = _selection; _dragStart = p; CaptureMouse(); return; }
            if (inside) { _mode = Mode.Moving; _dragOrigin = _selection; _dragStart = p; CaptureMouse(); return; }
            // Outside: start over with a new selection.
            Annotations.Clear();
        }
        _mode = Mode.Creating;
        _dragStart = p;
        _hasSelection = false;
        CaptureMouse();
        UpdateVisuals();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        switch (_mode)
        {
            case Mode.Creating:
                if (!_hasSelection && _hover is { } h) { _selection = h; _hasSelection = true; } // a click takes the window under the mouse
                _mode = _hasSelection ? Mode.Selected : Mode.Hover;
                // Pin and scroll captures act as soon as the area is chosen.
                if (_hasSelection && _captureMode == CaptureMode.Scroll) { Finish(CaptureAction.Scroll); return; }
                if (_hasSelection && _captureMode == CaptureMode.Pin) { Finish(CaptureAction.Pin); return; }
                if (_hasSelection && _captureMode == CaptureMode.Ocr) { Finish(CaptureAction.Ocr); return; }
                break;
            case Mode.Moving or Mode.Resizing:
                _mode = Mode.Selected;
                break;
            case Mode.Drawing:
                if (_drawing != null && (_drawing.Points.Count >= 2 || _drawing.Tool == AnnotationTool.Number))
                {
                    if (_drawing.Tool == AnnotationTool.Mosaic) MakeMosaic(_drawing);
                    if (_drawing.Bounds.Width >= 2 || _drawing.Bounds.Height >= 2 || _drawing.Tool is AnnotationTool.Number or AnnotationTool.Pen or AnnotationTool.Marker)
                        Annotations.Add(_drawing);
                }
                _drawing = null;
                _mode = Mode.Selected;
                _layer.InvalidateVisual();
                break;
        }
        UpdateVisuals();
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        CommitText();
        if (_hasSelection && _mode == Mode.Selected)
        {
            // Back to choosing (as in Snipaste): drop the selection and its marks.
            _hasSelection = false;
            Annotations.Clear();
            SetTool(AnnotationTool.None);
            _mode = Mode.Hover;
            UpdateVisuals();
            return;
        }
        Finish(CaptureAction.Cancel);
    }

    private void StartAnnotation(Point physical)
    {
        var q = ToSelection(physical);
        if (_tool == AnnotationTool.Text) { OpenTextEditor(q); return; }
        _drawing = new Annotation { Tool = _tool, Color = _color, Width = _width };
        _drawing.Points.Add(q);
        _mode = Mode.Drawing;
        CaptureMouse();
        if (_tool == AnnotationTool.Number)
        {
            Annotations.Add(_drawing);
            _drawing = null;
            _mode = Mode.Selected;
            ReleaseMouseCapture();
        }
        _layer.InvalidateVisual();
    }

    private void MakeMosaic(Annotation a)
    {
        var r = a.Bounds;
        var screen = new PixelRect(_selection.X + (int)r.X, _selection.Y + (int)r.Y, Math.Max(1, (int)r.Width), Math.Max(1, (int)r.Height));
        a.Patch = AnnotationRenderer.Pixelate(_shot, screen, Math.Max(6, (int)(_width * 3)));
    }

    private static Point Constrain(Point a, Point b, AnnotationTool tool)
    {
        var v = b - a;
        if (tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse)
        {
            double s = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
            return new Point(a.X + Math.Sign(v.X) * s, a.Y + Math.Sign(v.Y) * s);
        }
        // Lines snap to 45°.
        double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        return new Point(a.X + Math.Cos(angle) * v.Length, a.Y + Math.Sin(angle) * v.Length);
    }

    // ---- Selection handles ---------------------------------------------------------------------------

    /// <summary>Handle index (0 TL, 1 T, 2 TR, 3 R, 4 BR, 5 B, 6 BL, 7 L) under a physical point, or -1.</summary>
    private int HandleAt(Point p)
    {
        double grab = 6 * _scale;
        var s = _selection;
        double[] xs = [s.X, s.X + s.Width / 2.0, s.Right, s.Right, s.Right, s.X + s.Width / 2.0, s.X, s.X];
        double[] ys = [s.Y, s.Y, s.Y, s.Y + s.Height / 2.0, s.Bottom, s.Bottom, s.Bottom, s.Y + s.Height / 2.0];
        for (int i = 0; i < 8; i++)
            if (Math.Abs(p.X - xs[i]) <= grab && Math.Abs(p.Y - ys[i]) <= grab) return i;
        // Edges between handles resize too.
        bool inY = p.Y > s.Y && p.Y < s.Bottom, inX = p.X > s.X && p.X < s.Right;
        if (inY && Math.Abs(p.X - s.X) <= grab / 2) return 7;
        if (inY && Math.Abs(p.X - s.Right) <= grab / 2) return 3;
        if (inX && Math.Abs(p.Y - s.Y) <= grab / 2) return 1;
        if (inX && Math.Abs(p.Y - s.Bottom) <= grab / 2) return 5;
        return -1;
    }

    private static PixelRect Resize(PixelRect r, int handle, Point p)
    {
        double l = r.X, t = r.Y, rt = r.Right, b = r.Bottom;
        if (handle is 0 or 6 or 7) l = p.X;
        if (handle is 2 or 3 or 4) rt = p.X;
        if (handle is 0 or 1 or 2) t = p.Y;
        if (handle is 4 or 5 or 6) b = p.Y;
        return FromPoints(new Point(l, t), new Point(rt, b));
    }

    private Cursor CursorFor(Point p)
    {
        if (_tool != AnnotationTool.None && _selection.Contains((int)p.X, (int)p.Y)) return _tool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Pen;
        return HandleAt(p) switch
        {
            0 or 4 => Cursors.SizeNWSE,
            2 or 6 => Cursors.SizeNESW,
            1 or 5 => Cursors.SizeNS,
            3 or 7 => Cursors.SizeWE,
            _ => _selection.Contains((int)p.X, (int)p.Y) ? Cursors.SizeAll : Cursors.Cross,
        };
    }

    // ---- Keys ----------------------------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_textEditor != null) return; // the editor handles its own keys
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Escape: Finish(CaptureAction.Cancel); break;
            case Key.Enter when _hasSelection: Finish(CaptureAction.Copy); break;
            case Key.C when mods == ModifierKeys.Control && _hasSelection: Finish(CaptureAction.Copy); break;
            case Key.S when mods == ModifierKeys.Control && _hasSelection: Finish(CaptureAction.Save); break;
            case Key.T when mods == ModifierKeys.Control && _hasSelection: Finish(CaptureAction.Pin); break;
            case Key.T when mods == ModifierKeys.None && _hasSelection: Finish(CaptureAction.Ocr); break;
            case Key.Z when mods == ModifierKeys.Control:
                if (Annotations.Count > 0) { Annotations.RemoveAt(Annotations.Count - 1); _layer.InvalidateVisual(); }
                break;
            case Key.C when mods == ModifierKeys.None:
                // Copy the colour under the mouse, as #RRGGBB.
                int rgb = _shot.ColorAt(_cursorX, _cursorY);
                try { Clipboard.SetDataObject($"#{rgb:X6}", true); } catch (System.Runtime.InteropServices.COMException) { }
                Finish(CaptureAction.Cancel);
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when _hasSelection:
                int dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0, dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
                _selection = mods.HasFlag(ModifierKeys.Shift)
                    ? _selection with { Width = Math.Max(1, _selection.Width + dx), Height = Math.Max(1, _selection.Height + dy) }
                    : _selection with { X = _selection.X + dx, Y = _selection.Y + dy };
                _selection = ClampToMonitor(_selection);
                UpdateVisuals();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    public void Finish(CaptureAction action)
    {
        CommitText();
        _finish(this, action);
    }

    /// <summary>The selection with its annotations, at full resolution.</summary>
    public BitmapSource Render()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(_shot.Crop(_selection), new Rect(0, 0, _selection.Width, _selection.Height));
            AnnotationRenderer.DrawAll(dc, Annotations);
        }
        var bmp = new RenderTargetBitmap(_selection.Width, _selection.Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    // ---- Visuals -------------------------------------------------------------------------------------

    private void UpdateVisuals()
    {
        var full = new Rect(0, 0, Width, Height);
        PixelRect? shown = _hasSelection ? _selection : _mode == Mode.Hover ? _hover : null;
        if (shown is { } s && s.Width > 0)
        {
            var r = ToDip(s);
            _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(r));
            _frame.Visibility = Visibility.Visible;
            Canvas.SetLeft(_frame, r.X);
            Canvas.SetTop(_frame, r.Y);
            _frame.Width = Math.Max(1, r.Width);
            _frame.Height = Math.Max(1, r.Height);
            _sizeText.Text = $"{s.Width} × {s.Height}";
            _sizeLabel.Visibility = Visibility.Visible;
            Canvas.SetLeft(_sizeLabel, r.X);
            Canvas.SetTop(_sizeLabel, r.Y >= 24 ? r.Y - 24 : r.Y + 4);
            bool handles = _hasSelection && _mode != Mode.Creating;
            double[] xs = [r.Left, r.Left + r.Width / 2, r.Right, r.Right, r.Right, r.Left + r.Width / 2, r.Left, r.Left];
            double[] ys = [r.Top, r.Top, r.Top, r.Top + r.Height / 2, r.Bottom, r.Bottom, r.Bottom, r.Top + r.Height / 2];
            for (int i = 0; i < 8; i++)
            {
                _handles[i].Visibility = handles ? Visibility.Visible : Visibility.Collapsed;
                Canvas.SetLeft(_handles[i], xs[i] - 3.5);
                Canvas.SetTop(_handles[i], ys[i] - 3.5);
            }
        }
        else
        {
            _dim.Data = new RectangleGeometry(full);
            _frame.Visibility = Visibility.Collapsed;
            _sizeLabel.Visibility = Visibility.Collapsed;
            foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
        }

        bool showToolbar = _hasSelection && _mode is Mode.Selected or Mode.Drawing && _captureMode == CaptureMode.Copy;
        _toolbar.Visibility = showToolbar ? Visibility.Visible : Visibility.Collapsed;
        if (showToolbar) PlaceToolbar();
        _layer.InvalidateVisual();
        UpdateMagnifier();
    }

    private void PlaceToolbar()
    {
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _toolbar.DesiredSize;
        var r = ToDip(_selection);
        double x = Math.Clamp(r.Right - size.Width, 4, Width - size.Width - 4);
        double y = r.Bottom + 8;
        if (y + size.Height > Height - 4) y = r.Top - size.Height - 8;
        if (y < 4) y = r.Bottom - size.Height - 8;
        Canvas.SetLeft(_toolbar, x);
        Canvas.SetTop(_toolbar, y);
    }

    private void BuildMagnifier()
    {
        RenderOptions.SetBitmapScalingMode(_magImage, BitmapScalingMode.NearestNeighbor);
        var grid = new Grid();
        grid.Children.Add(_magImage);
        // Crosshair over the centre pixel.
        grid.Children.Add(new Rectangle { Width = 132, Height = 6, Fill = new SolidColorBrush(Color.FromArgb(0x50, 0x5B, 0x6A, 0xF0)), IsHitTestVisible = false });
        grid.Children.Add(new Rectangle { Width = 6, Height = 132, Fill = new SolidColorBrush(Color.FromArgb(0x50, 0x5B, 0x6A, 0xF0)), IsHitTestVisible = false });
        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(_magText);
        _magnifier.Child = stack;
        _magnifier.Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1F, 0x23, 0x28));
        _magnifier.BorderBrush = Brushes.White;
        _magnifier.BorderThickness = new Thickness(1);
        _magnifier.IsHitTestVisible = false;
        _root.Children.Add(_magnifier);
    }

    private void UpdateMagnifier()
    {
        bool show = _mode is Mode.Hover or Mode.Creating or Mode.Resizing;
        _magnifier.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        const int half = 11; // 22×22 pixels → 132 DIPs: each pixel is a 6-DIP square
        var src = new PixelRect(_cursorX - half, _cursorY - half, half * 2, half * 2);
        var clipped = ScreenPixels.Intersect(src, _shot.Bounds);
        if (clipped.Width > 0 && clipped.Height > 0) _magImage.Source = _shot.Crop(clipped);
        int rgb = _shot.ColorAt(_cursorX, _cursorY);
        _magText.Text = Loc.F("{0}, {1}   #{2:X6}\nC: copy colour · Esc: cancel", _cursorX - _monitor.Bounds.X, _cursorY - _monitor.Bounds.Y, rgb);
        var p = new Point((_cursorX - _monitor.Bounds.X) / _scale, (_cursorY - _monitor.Bounds.Y) / _scale);
        _magnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _magnifier.DesiredSize;
        double x = p.X + 20 + size.Width > Width ? p.X - 20 - size.Width : p.X + 20;
        double y = p.Y + 20 + size.Height > Height ? p.Y - 20 - size.Height : p.Y + 20;
        Canvas.SetLeft(_magnifier, x);
        Canvas.SetTop(_magnifier, y);
    }

    // ---- Toolbar -------------------------------------------------------------------------------------

    private void BuildToolbar()
    {
        var stack = new StackPanel();
        stack.Children.Add(_tools);
        stack.Children.Add(_palette);
        _toolbar.Child = stack;
        _toolbar.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        _toolbar.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
        _toolbar.BorderThickness = new Thickness(1);
        _toolbar.CornerRadius = new CornerRadius(8);
        _toolbar.Padding = new Thickness(4);
        _toolbar.Cursor = Cursors.Arrow;
        _toolbar.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.3 };
        _toolbar.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _toolbar.MouseLeftButtonUp += (_, e) => e.Handled = true;
        _toolbar.MouseRightButtonUp += (_, e) => e.Handled = true;

        AddTool(AnnotationTool.Rectangle, Loc.T("Rectangle"), Shape(new Rectangle { Width = 15, Height = 11, Stroke = Ink, StrokeThickness = 1.6 }));
        AddTool(AnnotationTool.Ellipse, Loc.T("Ellipse"), Shape(new Ellipse { Width = 15, Height = 12, Stroke = Ink, StrokeThickness = 1.6 }));
        AddTool(AnnotationTool.Arrow, Loc.T("Arrow (Shift: 45°)"), PathIcon("M3,15 L15,3 M8,3 L15,3 L15,10"));
        AddTool(AnnotationTool.Pen, Loc.T("Pen"), PathIcon("M2,13 C5,3 8,15 11,7 C12,4 14,4 16,6"));
        AddTool(AnnotationTool.Marker, Loc.T("Highlighter"), Shape(new Rectangle { Width = 16, Height = 7, Fill = new SolidColorBrush(Color.FromArgb(0x90, 0xFD, 0xD8, 0x35)) }));
        AddTool(AnnotationTool.Text, Loc.T("Text"), new TextBlock { Text = "A", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Center });
        AddTool(AnnotationTool.Mosaic, Loc.T("Mosaic"), PathIcon("M2,2 h6 v6 h-6 z M10,10 h6 v6 h-6 z", fill: true));
        AddTool(AnnotationTool.Number, Loc.T("Numbered marker"), new TextBlock { Text = "①", FontSize = 16, Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Center });
        _tools.Children.Add(new Border { Width = 1, Margin = new Thickness(5, 4, 5, 4), Background = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)) });
        AddAction("", Loc.T("Undo (Ctrl+Z)"), () => { if (Annotations.Count > 0) { Annotations.RemoveAt(Annotations.Count - 1); _layer.InvalidateVisual(); } });
        AddAction("\uE8C1", Loc.T("Recognise text (T)"), () => Finish(CaptureAction.Ocr));
        AddAction("", Loc.T("Scrolling capture: scrolls the area and stitches a long image"), () => Finish(CaptureAction.Scroll));
        AddAction("", Loc.T("Pin to the screen (Ctrl+T)"), () => Finish(CaptureAction.Pin));
        AddAction("", Loc.T("Save (Ctrl+S)"), () => Finish(CaptureAction.Save));
        _tools.Children.Add(new Border { Width = 1, Margin = new Thickness(5, 4, 5, 4), Background = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)) });
        AddAction("", Loc.T("Cancel (Esc)"), () => Finish(CaptureAction.Cancel));
        AddAction("", Loc.T("Copy (Enter, double-click)"), () => Finish(CaptureAction.Copy), accent: true);

        foreach (var c in AnnotationRenderer.Palette)
        {
            var swatch = new Border { Width = 18, Height = 18, Margin = new Thickness(3), CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(c), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Tag = c };
            swatch.MouseLeftButtonUp += (_, e) => { _color = (Color)swatch.Tag; UpdatePalette(); e.Handled = true; };
            _palette.Children.Add(swatch);
        }
        _palette.Children.Add(new Border { Width = 1, Margin = new Thickness(5, 4, 5, 4), Background = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)) });
        foreach (double w in AnnotationRenderer.Widths)
        {
            var dot = new Border { Width = 24, Height = 24, Margin = new Thickness(1), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = w };
            dot.Child = new Ellipse { Width = 3 + w, Height = 3 + w, Fill = Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dot.MouseLeftButtonUp += (_, e) => { _width = (double)dot.Tag; UpdatePalette(); e.Handled = true; };
            _palette.Children.Add(dot);
        }
        UpdatePalette();
        _root.Children.Add(_toolbar);
    }

    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28));
    private static readonly Brush Soft = new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xFC));

    private static FrameworkElement Shape(Shape s) { s.HorizontalAlignment = HorizontalAlignment.Center; s.VerticalAlignment = VerticalAlignment.Center; return s; }

    private static FrameworkElement PathIcon(string data, bool fill = false) => new System.Windows.Shapes.Path
    {
        Data = Geometry.Parse(data),
        Stroke = fill ? null : Ink,
        Fill = fill ? Ink : null,
        StrokeThickness = 1.6,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Width = 18,
        Height = 18,
        Stretch = Stretch.None,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Border Button(FrameworkElement icon, string tip)
    {
        var b = new Border { Width = 32, Height = 30, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Child = icon, ToolTip = tip, Cursor = Cursors.Hand, Margin = new Thickness(1, 0, 1, 0) };
        b.MouseEnter += (_, _) => { if (b.Tag is not true) b.Background = new SolidColorBrush(Color.FromArgb(0x14, 0, 0, 0)); };
        b.MouseLeave += (_, _) => { if (b.Tag is not true) b.Background = Brushes.Transparent; };
        return b;
    }

    private void AddTool(AnnotationTool tool, string tip, FrameworkElement icon)
    {
        var b = Button(icon, tip);
        b.MouseLeftButtonUp += (_, e) => { SetTool(_tool == tool ? AnnotationTool.None : tool); e.Handled = true; };
        _toolButtons[tool] = b;
        _tools.Children.Add(b);
    }

    private void AddAction(string glyph, string tip, Action action, bool accent = false)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = accent ? Brushes.White : Ink };
        var b = Button(icon, tip);
        if (accent) { b.Background = new SolidColorBrush(Color.FromRgb(0x4C, 0x5B, 0xD4)); b.Tag = true; }
        b.MouseLeftButtonUp += (_, e) => { action(); e.Handled = true; };
        _tools.Children.Add(b);
    }

    private void SetTool(AnnotationTool tool)
    {
        CommitText();
        _tool = tool;
        foreach (var (t, b) in _toolButtons)
        {
            b.Tag = t == tool;
            b.Background = t == tool ? Soft : Brushes.Transparent;
        }
        _palette.Visibility = tool == AnnotationTool.None ? Visibility.Collapsed : Visibility.Visible;
        if (_hasSelection) PlaceToolbar();
    }

    private void UpdatePalette()
    {
        foreach (var child in _palette.Children.OfType<Border>())
        {
            if (child.Tag is Color c) child.BorderBrush = c == _color ? Ink : Brushes.Gray;
            if (child.Tag is double w) child.Background = Math.Abs(w - _width) < 0.01 ? Soft : Brushes.Transparent;
            if (child.Tag is Color c2) child.BorderThickness = new Thickness(c2 == _color ? 2 : 1);
        }
    }

    // ---- Text tool -----------------------------------------------------------------------------------

    private void OpenTextEditor(Point selectionPoint)
    {
        var dip = ToDip(_selection);
        _textEditor = new TextBox
        {
            MinWidth = 60,
            FontSize = AnnotationRenderer.FontSizeFor(_width) / _scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(_color),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(_color),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            Tag = selectionPoint,
        };
        Canvas.SetLeft(_textEditor, dip.X + selectionPoint.X / _scale - 2);
        Canvas.SetTop(_textEditor, dip.Y + selectionPoint.Y / _scale - 2);
        _textEditor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseTextEditor(false); e.Handled = true; }
            else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { CloseTextEditor(true); e.Handled = true; }
        };
        _root.Children.Add(_textEditor);
        _textEditor.Focus();
    }

    private void CommitText() => CloseTextEditor(true);

    private void CloseTextEditor(bool keep)
    {
        if (_textEditor == null) return;
        var editor = _textEditor;
        _textEditor = null;
        _root.Children.Remove(editor);
        if (keep && editor.Text.Trim().Length > 0)
        {
            var a = new Annotation { Tool = AnnotationTool.Text, Color = _color, Width = _width, Text = editor.Text.TrimEnd() };
            a.Points.Add((Point)editor.Tag);
            Annotations.Add(a);
        }
        Focus();
        _layer.InvalidateVisual();
    }

    /// <summary>Draws the annotations (and the one being drawn) over the frozen screen.</summary>
    private sealed class AnnotationLayer(CaptureOverlay owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            if (!owner._hasSelection) return;
            var r = owner.ToDip(owner._selection);
            dc.PushClip(new RectangleGeometry(r));
            dc.PushTransform(new TranslateTransform(r.X, r.Y));
            dc.PushTransform(new ScaleTransform(1 / owner._scale, 1 / owner._scale));
            var all = owner._drawing == null ? owner.Annotations : owner.Annotations.Append(owner._drawing);
            if (owner._drawing is { Tool: AnnotationTool.Mosaic } m && m.Points.Count >= 2)
                dc.DrawRectangle(null, new Pen(Brushes.White, owner._scale) { DashStyle = DashStyles.Dash }, m.Bounds);
            AnnotationRenderer.DrawAll(dc, all.Where(a => a.Tool != AnnotationTool.Mosaic || a.Patch != null));
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }
    }
}
