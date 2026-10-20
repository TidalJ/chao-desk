using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ChaoDesk.Host.Desktop;
using ChaoDesk.Host.Localization;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Capture;

/// <summary>
/// An image pinned on top of everything (Snipaste's "paste"). Drag to move, wheel to zoom, Ctrl+wheel for opacity,
/// double-click or Esc to close. Hovering shows a toolbar to draw on it (pen, shapes, arrow, highlighter, text,
/// mosaic); marks become part of the pin and are kept. A layered window, so it can fade in and out smoothly.
/// </summary>
public sealed class PinWindow : Window
{
    private const double Shadow = 10; // DIPs of transparent margin around the image for the shadow

    private readonly Image _view = new() { Stretch = Stretch.Fill };
    private readonly Grid _surface = new();
    private readonly MarkLayer _marks;
    private readonly PinToolbar _toolbar;
    private readonly Action _changed;
    private readonly Action<BitmapSource> _copy;
    private readonly Action<BitmapSource, Window?> _save;
    private BitmapSource _image;
    private ScreenPixels? _pixels; // for mosaics, made on first use
    private double _zoom = 1;
    private double _opacity = 1;
    private double _scale = 1;
    private PixelRect _initial;
    private Annotation? _drawing;
    private TextBox? _textEditor;

    public string Id { get; }
    public List<Annotation> Marks { get; } = new();
    public AnnotationTool Tool { get; private set; } = AnnotationTool.None;
    public Color MarkColor { get; set; } = AnnotationRenderer.Palette[0];
    public double MarkWidth { get; set; } = AnnotationRenderer.Widths[1];
    public double Zoom => _zoom;

    /// <summary>Image pixels per DIP on screen at the current zoom: marks are sized so they look the same at any zoom.</summary>
    private double PxPerDip => _surface.ActualWidth > 0 ? _image.PixelWidth / _surface.ActualWidth : 1;

    public void CopyImage() => _copy(Composed);
    public double PinOpacity => _opacity;

    /// <summary>The image with its marks, at full resolution.</summary>
    public BitmapSource Composed
    {
        get
        {
            if (Marks.Count == 0) return _image;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(_image, new Rect(0, 0, _image.PixelWidth, _image.PixelHeight));
                AnnotationRenderer.DrawAll(dc, Marks);
            }
            var bmp = new RenderTargetBitmap(_image.PixelWidth, _image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }

    /// <summary>Where the image's top-left corner is on screen, in physical pixels.</summary>
    public (int X, int Y) ImagePosition
    {
        get
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r)) return (_initial.X, _initial.Y);
            int margin = (int)Math.Round(Shadow * _scale);
            return (r.Left + margin, r.Top + margin);
        }
    }

    /// <param name="at">The image's position on screen (physical pixels) — exactly over where it was captured.</param>
    /// <param name="changed">Called after anything that should be saved (move, zoom, marks, close).</param>
    /// <summary>Reads the text in the pin (with its marks).</summary>
    public Action<BitmapSource>? Recognize { get; }

    public void RecognizeText() => Recognize?.Invoke(Composed);

    public PinWindow(string id, BitmapSource image, PixelRect at, double zoom, double opacity, Action changed, Action<BitmapSource> copy, Action<BitmapSource, Window?> save, Action<BitmapSource>? recognize = null)
    {
        Recognize = recognize; // before the menu and toolbar are built
        Id = id;
        _image = image;
        _initial = at;
        _zoom = zoom;
        _opacity = Math.Clamp(opacity, 0.2, 1);
        _changed = changed;
        _copy = copy;
        _save = save;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        UseLayoutRounding = true;
        Title = Loc.T("Pinned image");
        Opacity = 0; // fades in when shown

        _view.Source = image;
        RenderOptions.SetBitmapScalingMode(_view, BitmapScalingMode.HighQuality);
        _marks = new MarkLayer(this);
        _surface.Children.Add(_view);
        _surface.Children.Add(_marks);
        Content = new Border
        {
            Margin = new Thickness(Shadow),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, 0x5B, 0x6A, 0xF0)),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35 },
            Child = _surface,
        };
        _toolbar = new PinToolbar(this);
        ContextMenu = BuildMenu();

        SourceInitialized += (_, _) =>
        {
            _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            ApplyZoom(keepCenter: false);
            int margin = (int)Math.Round(Shadow * _scale);
            SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, at.X - margin, at.Y - margin, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        };
        DpiChanged += (_, e) => { _scale = e.NewDpi.DpiScaleX; ApplyZoom(keepCenter: false); };
        _surface.MouseLeftButtonDown += OnSurfaceDown;
        _surface.MouseMove += OnSurfaceMove;
        _surface.MouseLeftButtonUp += OnSurfaceUp;
        MouseWheel += OnWheel;
        KeyDown += OnKey;
        MouseEnter += (_, _) => _toolbar.Appear();
        LocationChanged += (_, _) => _toolbar.Follow();
        Closed += (_, _) => { _toolbar.Close(); _changed(); };
    }

    // ---- Showing and hiding (animated) ---------------------------------------------------------------

    public void FadeIn(int delayMs = 0)
    {
        if (!IsVisible) { Opacity = 0; Show(); }
        var anim = new DoubleAnimation(_opacity, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(delayMs), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        BeginAnimation(OpacityProperty, anim);
    }

    public void FadeOut(int delayMs = 0)
    {
        _toolbar.Vanish();
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)) { BeginTime = TimeSpan.FromMilliseconds(delayMs), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        anim.Completed += (_, _) => { if (Opacity == 0) Hide(); };
        BeginAnimation(OpacityProperty, anim);
    }

    private void SetOpacity(double value)
    {
        _opacity = Math.Clamp(value, 0.2, 1);
        BeginAnimation(OpacityProperty, null);
        Opacity = _opacity;
        _changed();
    }

    // ---- Size --------------------------------------------------------------------------------------

    private void ApplyZoom(bool keepCenter)
    {
        double w = _image.PixelWidth / _scale * _zoom, h = _image.PixelHeight / _scale * _zoom;
        double cx = Left + ActualWidth / 2, cy = Top + ActualHeight / 2;
        _surface.Width = w;
        _surface.Height = h;
        Width = w + 2 * Shadow + 2;
        Height = h + 2 * Shadow + 2;
        if (keepCenter && ActualWidth > 0) { Left = cx - Width / 2; Top = cy - Height / 2; }
        ToolTip = Tool == AnnotationTool.None ? Loc.F("{0:P0} · wheel: zoom · Ctrl+wheel: opacity ({1:P0}) · double-click: close", _zoom, _opacity) : null;
        _marks.InvalidateVisual();
        _toolbar.Follow();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) SetOpacity(_opacity + (e.Delta > 0 ? 0.1 : -0.1));
        else { _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.1, 8); ApplyZoom(keepCenter: true); _changed(); }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_textEditor != null) return;
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.Escape) { if (Tool != AnnotationTool.None) SetTool(AnnotationTool.None); else Close(); }
        else if (e.Key == Key.C && mods == ModifierKeys.Control) CopyImage();
        else if (e.Key == Key.S && mods == ModifierKeys.Control) _save(Composed, this);
        else if (e.Key == Key.Z && mods == ModifierKeys.Control) Undo();
        else if (e.Key == Key.T && mods == ModifierKeys.None && Tool == AnnotationTool.None) RecognizeText();
        else if (e.Key is Key.D0 or Key.NumPad0 && mods == ModifierKeys.Control) { _zoom = 1; ApplyZoom(true); _changed(); }
        else return;
        e.Handled = true;
    }

    // ---- Drawing on the pin ------------------------------------------------------------------------------

    public void SetTool(AnnotationTool tool)
    {
        CommitText();
        Tool = tool;
        _surface.Cursor = tool == AnnotationTool.None ? Cursors.SizeAll : tool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Pen;
        ToolTip = null;
        _toolbar.Refresh();
    }

    public void Undo()
    {
        if (Marks.Count == 0) return;
        Marks.RemoveAt(Marks.Count - 1);
        _marks.InvalidateVisual();
        _changed();
    }

    /// <summary>Surface DIPs → image pixels.</summary>
    private Point ToImage(Point p) => new(
        Math.Clamp(p.X / _surface.ActualWidth * _image.PixelWidth, 0, _image.PixelWidth),
        Math.Clamp(p.Y / _surface.ActualHeight * _image.PixelHeight, 0, _image.PixelHeight));

    private void OnSurfaceDown(object sender, MouseButtonEventArgs e)
    {
        CommitText();
        if (Tool == AnnotationTool.None)
        {
            if (e.ClickCount == 2) { Close(); return; }
            DragMove();
            _changed();
            return;
        }
        var q = ToImage(e.GetPosition(_surface));
        if (Tool == AnnotationTool.Text) { OpenTextEditor(e.GetPosition(_surface), q); e.Handled = true; return; }
        _drawing = new Annotation { Tool = Tool, Color = MarkColor, Width = MarkWidth * PxPerDip };
        _drawing.Points.Add(q);
        if (Tool == AnnotationTool.Number) { Marks.Add(_drawing); _drawing = null; _marks.InvalidateVisual(); _changed(); return; }
        _surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnSurfaceMove(object sender, MouseEventArgs e)
    {
        if (_drawing == null) return;
        var q = ToImage(e.GetPosition(_surface));
        if (_drawing.Tool is AnnotationTool.Pen or AnnotationTool.Marker) _drawing.Points.Add(q);
        else if (_drawing.Points.Count == 1) _drawing.Points.Add(q);
        else _drawing.Points[^1] = q;
        _marks.InvalidateVisual();
    }

    private void OnSurfaceUp(object sender, MouseButtonEventArgs e)
    {
        if (_drawing == null) return;
        _surface.ReleaseMouseCapture();
        if (_drawing.Points.Count >= 2)
        {
            if (_drawing.Tool == AnnotationTool.Mosaic)
            {
                _pixels ??= PixelsOf(_image);
                var r = _drawing.Bounds;
                _drawing.Patch = AnnotationRenderer.Pixelate(_pixels, new PixelRect((int)r.X, (int)r.Y, Math.Max(1, (int)r.Width), Math.Max(1, (int)r.Height)), Math.Max(6, (int)(_drawing.Width * 3)));
            }
            Marks.Add(_drawing);
            _changed();
        }
        _drawing = null;
        _marks.InvalidateVisual();
    }

    private static ScreenPixels PixelsOf(BitmapSource image)
    {
        var src = image.Format == PixelFormats.Bgr32 || image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Pbgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var data = new byte[src.PixelWidth * src.PixelHeight * 4];
        src.CopyPixels(data, src.PixelWidth * 4, 0);
        return new ScreenPixels { Bounds = new PixelRect(0, 0, src.PixelWidth, src.PixelHeight), Data = data };
    }

    private void OpenTextEditor(Point surfacePoint, Point imagePoint)
    {
        _textEditor = new TextBox
        {
            MinWidth = 50,
            FontSize = AnnotationRenderer.FontSizeFor(MarkWidth * PxPerDip) / PxPerDip,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MarkColor),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(MarkColor),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(surfacePoint.X - 2, surfacePoint.Y - 2, 0, 0),
            Tag = imagePoint,
        };
        _textEditor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseText(false); e.Handled = true; }
            else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { CloseText(true); e.Handled = true; }
        };
        _surface.Children.Add(_textEditor);
        Activate();
        _textEditor.Focus();
    }

    private void CommitText() => CloseText(true);

    private void CloseText(bool keep)
    {
        if (_textEditor == null) return;
        var editor = _textEditor;
        _textEditor = null;
        _surface.Children.Remove(editor);
        if (keep && editor.Text.Trim().Length > 0)
        {
            var a = new Annotation { Tool = AnnotationTool.Text, Color = MarkColor, Width = MarkWidth * PxPerDip, Text = editor.Text.TrimEnd() };
            a.Points.Add((Point)editor.Tag);
            Marks.Add(a);
            _changed();
        }
        _marks.InvalidateVisual();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        void Add(string header, string gesture, Action action)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        Add(Loc.T("Copy"), "Ctrl+C", CopyImage);
        Add(Loc.T("Save as…"), "Ctrl+S", () => _save(Composed, this));
        if (Recognize != null) Add(Loc.T("Recognise text"), "T", RecognizeText);
        Add(Loc.T("Undo last mark"), "Ctrl+Z", Undo);
        Add(Loc.T("Actual size"), "Ctrl+0", () => { _zoom = 1; ApplyZoom(true); _changed(); });
        Add(Loc.T("Opacity 100 %"), "", () => SetOpacity(1));
        menu.Items.Add(new Separator());
        Add(Loc.T("Close"), "Esc", Close);
        return menu;
    }

    /// <summary>Draws the marks (and the one in progress) over the image, scaled to the current zoom.</summary>
    private sealed class MarkLayer(PinWindow owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            if (owner._image.PixelWidth == 0 || ActualWidth == 0) return;
            double s = ActualWidth / owner._image.PixelWidth;
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
            dc.PushTransform(new ScaleTransform(s, s));
            var all = owner._drawing == null ? owner.Marks : owner.Marks.Append(owner._drawing);
            AnnotationRenderer.DrawAll(dc, all.Where(a => a.Tool != AnnotationTool.Mosaic || a.Patch != null));
            dc.Pop();
            dc.Pop();
        }
    }
}

/// <summary>
/// The pin's toolbar: fades in below the pin when the mouse arrives, fades out a moment after it has left both.
/// Never takes the keyboard focus from other windows.
/// </summary>
internal sealed class PinToolbar : Window
{
    private static (AnnotationTool Tool, string Glyph, string Tip)[] Tools =>
    [
        (AnnotationTool.None, "", Loc.T("Move (drag)")), (AnnotationTool.Pen, "", Loc.T("Pen")), (AnnotationTool.Rectangle, "", Loc.T("Rectangle")),
        (AnnotationTool.Arrow, "", Loc.T("Arrow")), (AnnotationTool.Marker, "", Loc.T("Highlighter")), (AnnotationTool.Text, "", Loc.T("Text")),
        (AnnotationTool.Mosaic, "", Loc.T("Mosaic")), (AnnotationTool.Number, "", Loc.T("Numbered marker")),
    ];

    private readonly PinWindow _pin;
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    private readonly System.Windows.Threading.DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private int _away;

    public PinToolbar(PinWindow pin)
    {
        _pin = pin;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        Opacity = 0;
        Content = new Border
        {
            Margin = new Thickness(8),
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Opacity = 0.25 },
            Child = _row,
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        };
        _watch.Tick += (_, _) => CheckMouse();
        Build();
    }

    private void Build()
    {
        _row.Children.Clear();
        foreach (var (tool, glyph, tip) in Tools)
            _row.Children.Add(Button(glyph, tip, () => _pin.SetTool(_pin.Tool == tool ? AnnotationTool.None : tool), _pin.Tool == tool));
        _row.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 5, 4, 5), Background = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)) });
        foreach (var c in AnnotationRenderer.Palette.Take(6))
        {
            var swatch = new Border
            {
                Width = 16, Height = 16, Margin = new Thickness(3, 0, 3, 0), CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(c), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                BorderBrush = c == _pin.MarkColor ? Brushes.Black : new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), BorderThickness = new Thickness(c == _pin.MarkColor ? 2 : 1),
            };
            swatch.MouseLeftButtonUp += (_, _) => { _pin.MarkColor = c; if (_pin.Tool == AnnotationTool.None) _pin.SetTool(AnnotationTool.Pen); Build(); };
            _row.Children.Add(swatch);
        }
        _row.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 5, 4, 5), Background = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)) });
        _row.Children.Add(Button("", Loc.T("Undo (Ctrl+Z)"), _pin.Undo));
        _row.Children.Add(Button("", Loc.T("Copy (Ctrl+C)"), _pin.CopyImage));
        if (_pin.Recognize != null) _row.Children.Add(Button("\uE8C1", Loc.T("Recognise text (T)"), _pin.RecognizeText));
        _row.Children.Add(Button("", Loc.T("Close (Esc, double-click)"), _pin.Close));
    }

    private static Border Button(string glyph, string tip, Action action, bool active = false)
    {
        var ink = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28));
        var b = new Border
        {
            Width = 28, Height = 26, CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand, ToolTip = tip,
            Background = active ? new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xFC)) : Brushes.Transparent,
            Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Foreground = ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        if (!active)
        {
            b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromArgb(0x14, 0, 0, 0));
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        }
        b.MouseLeftButtonUp += (_, _) => action();
        return b;
    }

    public void Refresh() => Build();

    /// <summary>Fade in under the pin (above it when there is no room below).</summary>
    public void Appear()
    {
        if (!_pin.IsVisible) return;
        _away = 0;
        if (!IsVisible) { Show(); Follow(); }
        _watch.Start();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
    }

    public void Vanish()
    {
        _watch.Stop();
        if (!IsVisible) return;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        anim.Completed += (_, _) => { if (Opacity == 0) Hide(); };
        BeginAnimation(OpacityProperty, anim);
    }

    public void Follow()
    {
        if (!IsVisible) return;
        UpdateLayout();
        double x = _pin.Left + (_pin.ActualWidth - ActualWidth) / 2;
        double below = _pin.Top + _pin.ActualHeight - 6;
        double y = below + ActualHeight > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight ? _pin.Top - ActualHeight + 6 : below;
        Left = Math.Max(SystemParameters.VirtualScreenLeft, x);
        Top = y;
    }

    /// <summary>Hide once the mouse has been away from both the pin and the toolbar for ~0.6 s (unless drawing).</summary>
    private void CheckMouse()
    {
        if (!_pin.IsVisible) { Vanish(); return; }
        GetCursorPos(out POINT p);
        bool over = Contains(this, p) || Contains(_pin, p);
        _away = over ? 0 : _away + 1;
        if (_away >= 4 && _pin.Tool == AnnotationTool.None) Vanish();
    }

    private static bool Contains(Window w, POINT p)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT r) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }
}
