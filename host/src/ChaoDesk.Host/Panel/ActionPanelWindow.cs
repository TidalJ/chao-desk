using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using ChaoDesk.Host.Capture;
using ChaoDesk.Host.Clips;
using ChaoDesk.Host.Desktop;
using ChaoDesk.Host.Input;
using ChaoDesk.Host.Launcher;
using ChaoDesk.Host.Localization;
using ChaoDesk.Host.Settings;
using ChaoDesk.Host.Translate;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;
using ShapePath = System.Windows.Shapes.Path;

namespace ChaoDesk.Host.Panel;

/// <summary>Native shortcut controls above a live system-composed backdrop.</summary>
public sealed partial class ActionPanelWindow : Window
{
    private readonly ClipboardHistory _history;
    private readonly ClipboardMonitor _monitor;
    private readonly Func<bool> _clipboardEnabled;
    private readonly Action<string> _execute;
    private readonly Func<string, CancellationToken, Task<Translation>> _translate;
    private readonly Grid _root = new();
    private readonly Grid _surface = new();
    private readonly Border _tint = new();
    private readonly GlassScene _glassScene;
    private Rect _captureBounds;
    private readonly Func<GlassOptions> _glassOptions;
    private readonly Border _shine;
    private bool _nativeMoving;
    private bool _pointerInside;
    private readonly GlassLensEffect _lens = new();
    private GlassBackdrop? _glass;
    private GlassTexture _glassTexture = new();
    private long _uploadCount;
    private int _uploadDeferrals;
    internal int GlassUploadDeferrals => _uploadDeferrals;
    private double _uploadMilliseconds;
    private bool UsesLens => _material() is "liquid" or "soft" or "smoked" && !SystemParameters.HighContrast;
    internal int GlassFrames => _glass?.Delivered ?? 0;
    internal long GlassUploads => _uploadCount;
    internal Rect CapturedBounds => _captureBounds;
    internal Rect PaneScreenBounds
    {
        get
        {
            if (!GetWindowRect(new WindowInteropHelper(this).Handle, out RECT r)) return Rect.Empty;
            // Prime runs while hidden, before PointToScreen has a presentation
            // source. Use the arranged client geometry and physical HWND bounds.
            double sx=(r.Right-r.Left)/Math.Max(1,_root.ActualWidth), sy=(r.Bottom-r.Top)/Math.Max(1,_root.ActualHeight);
            var offset=_surface.TranslatePoint(new Point(),_root);
            return new Rect(r.Left+offset.X*sx,r.Top+offset.Y*sy,_surface.ActualWidth*sx,_surface.ActualHeight*sy);
        }
    }
    internal System.Windows.Media.Media3D.Point4D CaptureMapping => _lens.Capture;
    internal System.Windows.Media.Media3D.Point4D RawCaptureMapping => _glassScene.RawCaptureMapping;
    internal Rect GlassLensBounds => _glassScene.LensBounds;
    private readonly PanelLayout _actions;
    private readonly Action _openSettings;
    private double _availableHeight = 700;
    private readonly StackPanel _body = new();
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _heading = new() { FontSize = 16, FontWeight = FontWeights.SemiBold };
    private readonly Button _home;
    private readonly Button _settings;
    private Brush _ink = Brushes.Black, _muted = Brushes.Gray, _hover = Brushes.Transparent;
    private bool _previewingMaterial, _switching, _closed, _liveBackdrop;
    private readonly Func<string> _material;
    private string _page = "home";
    private IntPtr _previous;
    private CancellationTokenSource? _translation;

    public ActionPanelWindow(ClipboardHistory history, ClipboardMonitor monitor, Func<bool> clipboardEnabled,
        Action<string> execute, Func<string, CancellationToken, Task<Translation>> translate, Func<string>? material = null, PanelLayout? actions = null, Func<GlassOptions>? glassOptions = null, Action? openSettings = null)
    {
        _history = history; _monitor = monitor; _clipboardEnabled = clipboardEnabled; _execute = execute; _translate = translate; _material = material ?? (() => "soft");
        _actions = actions ?? new PanelLayout();
        _openSettings = openSettings ?? (() => _execute("settings"));
        _glassOptions = glassOptions ?? (() => new());
        Title = Loc.T("Action panel"); Width = PanelLayout.Width; Height = _actions.HomeHeight;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = false;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(-1), UseAeroCaptionButtons = false });
        Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");
        FontSize = 14; UseLayoutRounding = true; SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        Content = _root;
        _ink = Brushes.White; _muted = Brushes.White; _hover = new SolidColorBrush(Color.FromArgb(18,255,255,255));
        _heading.Effect = GlyphContrast(); _status.Effect = GlyphContrast();
        _surface.Background = Brushes.Transparent;
        _surface.PreviewMouseLeftButtonDown += DragPanel;
        var frame = new Border { Child = _surface };
        _root.Children.Add(frame);
        _glassScene = new GlassScene(_lens);
        _surface.Children.Add(_glassScene);
        _surface.Children.Add(_tint);
        var shine = _shine = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(24), IsHitTestVisible = false,
            BorderBrush = new LinearGradientBrush(Color.FromArgb(130, 255, 255, 255), Color.FromArgb(18, 255, 255, 255), 90) };
        var layout = new Grid { Margin = new Thickness(16, 15, 16, 12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _surface.Children.Add(layout); _surface.Children.Add(shine);
        _surface.SizeChanged += (_, _) => UpdateGlassOptions();
        _surface.SizeChanged += (_, _) => UpdateLensGeometry();
        _body.SizeChanged += (_, _) => UpdateLensGeometry();
        _surface.MouseMove += (_, e) => UpdateGlassPointer(e.GetPosition(_surface));
        _surface.MouseLeave += (_, _) => SetGlassHover(false);
        var head = new DockPanel { MinHeight = 34, Margin = new Thickness(0, 0, 0, 10) };
        _settings = IconButton(PanelAction.Find("settings")!.Icon, Loc.Pick("Panel settings", "面板设置"), () => { Dismiss(); _openSettings(); });
        AutomationProperties.SetAutomationId(_settings, "PanelSettings");
        DockPanel.SetDock(_settings, Dock.Right); head.Children.Add(_settings);
        _home = IconButton("M14,5 L7,12 L14,19 M7,12 L22,12", Loc.Pick("Back", "返回"), () => Navigate("home"));
        _home.Margin = new Thickness(0,0,6,0);
        DockPanel.SetDock(_home,Dock.Left); head.Children.Add(_home);
        _heading.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(_heading); layout.Children.Add(head);
        head.Background = Brushes.Transparent;
        head.ToolTip = Loc.T("Drag to move the panel");
        var scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.ScrollChanged += (_, _) => UpdateLensGeometry();
        Grid.SetRow(scroll, 2); layout.Children.Add(scroll);
        var statusStyle = new Style(typeof(TextBlock));
        var emptyStatus = new Trigger { Property = TextBlock.TextProperty, Value = "" };
        emptyStatus.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed)); statusStyle.Triggers.Add(emptyStatus);
        _status.Style = statusStyle;
        _status.Margin = new Thickness(0, 9, 0, 7); AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Grid.SetRow(_status, 3); layout.Children.Add(_status);
        PopupPlacement.IgnoreAltMenu(this);
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (!_previewingMaterial && !_switching) Dismiss(false); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { _previewingMaterial=false; _translation?.Cancel(); StopGlass(); } };
        SourceInitialized += (_, _) => {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(GlassWindowMessage);
            UpdateNativeBackdrop();
        };
        DpiChanged += (_, _) => UpdateLensGeometry();
        _history.Changed += HistoryChanged; ThemeManager.Changed += RefreshMaterial; Loc.Changed += LanguageChanged;
        Closed += (_, _) => { _closed = true; StopGlass(); _translation?.Cancel(); _translation?.Dispose(); _history.Changed -= HistoryChanged; ThemeManager.Changed -= RefreshMaterial; Loc.Changed -= LanguageChanged; };
        ApplyColors();
    }

    private void ApplyColors()
    {
        _ink = Brushes.White; _muted = Brushes.White;
        _hover = new SolidColorBrush(Color.FromArgb(18,255,255,255));
        // Scope the glass palette to this window, including in-panel editors.
        foreach (string name in new[] { "InkBrush", "MutedBrush", "AccentBrush" }) Resources[name] = Brushes.White;
        Resources["ControlBrush"] = new SolidColorBrush(Color.FromArgb(110,18,22,28));
        Resources["AccentSoftBrush"] = new SolidColorBrush(Color.FromArgb(28,255,255,255));
        Resources["HoverBrush"] = _hover;
        Resources[SystemColors.HighlightBrushKey] = Resources["AccentSoftBrush"];
        Resources[SystemColors.HighlightTextBrushKey] = Brushes.White;
        Resources[SystemColors.WindowBrushKey] = new SolidColorBrush(Color.FromRgb(30,34,40));
        Resources[SystemColors.WindowTextBrushKey] = Brushes.White;
        Foreground = _ink; _status.Foreground = _muted; _heading.Foreground = _ink;
        _tint.Background = !_liveBackdrop || SystemParameters.HighContrast
            ? new SolidColorBrush(Color.FromRgb(24,28,34)) : Brushes.Transparent;
        Refresh();
    }

    // Optical changes share one capture session and keep the current image visible.
    public void RefreshMaterial()
    {
        if (UsesLens && _liveBackdrop && _glass != null)
        {
            UpdateGlassOptions();
            return;
        }
        StopGlass(); UpdateNativeBackdrop();
        if (IsVisible) { StartGlass(); _glass?.Start(); }
    }

    private void UpdateNativeBackdrop()
    {
        _liveBackdrop = NativePanelBackdrop.Apply(this, _material(), ThemeManager.IsDark);
        ApplyColors();
        UpdateGlassOptions();
    }

    public void UpdateGlassOptions()
    {
        var options = _glassOptions().Normalize();
        double w = Math.Max(1, _surface.ActualWidth), h = Math.Max(1, _surface.ActualHeight);
        _lens.Geometry = new System.Windows.Media.Media3D.Point4D(w, h, options.Radius, 1);
        _lens.Optics = new System.Windows.Media.Media3D.Point4D(options.Displacement, 0, _material() == "soft" ? 1 : 0, _material() == "smoked" ? 1 : 0);
        _lens.Finish = new System.Windows.Media.Media3D.Point4D(options.Aberration, options.Saturation/100, options.Highlight, options.Mode == "shader" ? 1 : 0);
        _lens.SetMode(options.Mode, (int)w, (int)h);
        // CSS blur() is Gaussian sigma; WPF Radius spans approximately three sigma.
        double blurRadius = Math.Min(100, 3*(4+options.Blur*32));
        var dpi = VisualTreeHelper.GetDpi(this);
        _glassScene.Configure(new Size(w, h), dpi, blurRadius, options.Displacement, options.Aberration);
        _glass?.SetOverscan((int)Math.Ceiling(96+Math.Max(dpi.DpiScaleX,dpi.DpiScaleY)*(options.Displacement*.5*(1+options.Aberration*.1)+blurRadius)));
        // One outer silhouette. Shader opacity and native DWM rounding must not cut it again.
        double radius = Math.Min(options.Radius, Math.Min(w,h)/2);
        _surface.Clip = new RectangleGeometry(new Rect(_surface.RenderSize), radius, radius);
        _shine.CornerRadius = new CornerRadius(options.Radius);
        _shine.Visibility = UsesLens ? Visibility.Collapsed : Visibility.Visible;
    }

    private IntPtr GlassWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0047) { // WM_WINDOWPOSCHANGED, including native DragMove
            if (_nativeMoving) _glass?.PresentLatestDuringMove();
            UpdateCaptureMapping();
        }
        if (message == 0x0231) BeginGlassMotion();
        if (message == 0x0232) EndGlassMotion();
        return IntPtr.Zero;
    }

    private void UpdateCaptureMapping()
    {
        if (_captureBounds.IsEmpty || _captureBounds.Width <= 0) return;
        _glassScene.UpdateMapping(PaneScreenBounds);
    }

    private void UpdateGlassPointer(Point position)
    {
        if (_nativeMoving) return;
        int slot = 0;
        var bounds = new System.Windows.Media.Media3D.Point4D(0,0,-1,-1);
        var grid = _body.Children.OfType<UniformGrid>().FirstOrDefault();
        if (grid != null) for (int i=0;i<grid.Children.Count;i++) {
            if (grid.Children[i] is not Button button || !button.IsEnabled) continue;
            var point = button.TranslatePoint(new Point(), _surface);
            if (new Rect(point,button.RenderSize).Contains(position)) {
                slot=i+1; bounds=new System.Windows.Media.Media3D.Point4D(point.X,point.Y,button.ActualWidth,button.ActualHeight); break;
            }
        }
        _lens.HoverBounds = bounds;
        _lens.Hover = new System.Windows.Media.Media3D.Point4D(position.X/Math.Max(1,_surface.ActualWidth),position.Y/Math.Max(1,_surface.ActualHeight),0,slot);
        SetGlassHover(true);
    }

    private void SetGlassHover(bool active)
    {
        if (_pointerInside == active) return;
        _pointerInside = active;
        double target = active ? 1 : 0;
        _lens.BeginAnimation(GlassLensEffect.HoverAmountProperty, SystemParameters.ClientAreaAnimation
            ? new DoubleAnimation(target, TimeSpan.FromMilliseconds(active ? 100 : 140)) { EasingFunction = new QuadraticEase { EasingMode=EasingMode.EaseOut } }
            : null);
        _lens.HoverAmount = target;
    }

    private void RefreshGlassPointer()
    {
        if (_nativeMoving) return;
        if (IsVisible && _surface.IsMouseOver)
            UpdateGlassPointer(Mouse.GetPosition(_surface));
        else SetGlassHover(false);
    }

    private void ResetGlassInteraction()
    {
        var pointer = _lens.Pointer;
        _lens.Pointer = new System.Windows.Media.Media3D.Point4D(pointer.X, pointer.Y, 0, 0);
        var hover = _lens.Hover;
        _lens.Hover = new System.Windows.Media.Media3D.Point4D(hover.X, hover.Y, 0, 0);
        _lens.HoverBounds = new System.Windows.Media.Media3D.Point4D(0,0,-1,-1);
        _lens.BeginAnimation(GlassLensEffect.HoverAmountProperty, null);
        _lens.HoverAmount = 0;
        _pointerInside = false;
    }

    internal void BeginGlassMotion()
    {
        if (_nativeMoving) return;
        _nativeMoving = true; _glass?.SetMoving(true); SetGlassHover(false);
    }

    internal void EndGlassMotion()
    {
        bool wasMoving = _nativeMoving;
        _nativeMoving = false; _glass?.SetMoving(false);
        if (wasMoving) RefreshGlassPointer();
    }

    private void StartGlass()
    {
        if (!UsesLens || _glass != null) return;
        try
        {
            if (!_liveBackdrop) throw new NotSupportedException("Transparent DWM composition is unavailable");
            if (!RenderCapability.IsPixelShaderVersionSupported(3, 0)) throw new NotSupportedException("Pixel shader 3 is unavailable");
            _glass = new GlassBackdrop(new WindowInteropHelper(this).Handle, Dispatcher, UploadGlass, ex =>
            {
                Log.Warning(ex, "Local glass sampling failed"); GlassUnavailable();
            });
            UpdateGlassOptions();
            _uploadCount = 0; _uploadMilliseconds = 0; _uploadDeferrals = 0;
            _glass.Prime(); // hidden first frame: no opaque-to-transparent switch after opening
            Log.Information("Refractive glass primed before show: {Width}x{Height}", _glassTexture.Bitmap?.PixelWidth, _glassTexture.Bitmap?.PixelHeight);
            var dpi = VisualTreeHelper.GetDpi(this);
            Log.Information("Refractive glass lens domain: {Width:0.##}x{Height:0.##} physical pixels; full-resolution blur source retained (not GPU timing)",
                _glassScene.LensBounds.Width*dpi.DpiScaleX, _glassScene.LensBounds.Height*dpi.DpiScaleY);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Refractive glass could not initialize"); GlassUnavailable();
        }
    }

    private void GlassUnavailable()
    {
        StopGlass();
        _tint.Background = new SolidColorBrush(Color.FromRgb(24,28,34));
        _status.Text = Loc.Pick("Live refraction unavailable. Using a solid surface; try another display or material in Settings.",
            "实时折射不可用，已使用实色面板。可更换显示器或在设置中选择其他材质。");
    }

    private bool UploadGlass(GlassBackdrop.Frame frame)
    {
        if (_closed) return true;
        long uploadStart = Stopwatch.GetTimestamp();
        if (!_glassTexture.TryUpload(frame)) { _uploadDeferrals++; return false; }
        _captureBounds = _glassTexture.Bounds;
        _glassScene.SetFrame(_glassTexture.Bitmap!, _captureBounds, PaneScreenBounds);
        _uploadMilliseconds += Stopwatch.GetElapsedTime(uploadStart).TotalMilliseconds;
        _uploadCount++;
        return true;
    }

    private void StopGlass()
    {
        EndGlassMotion(); SetGlassHover(false);
        if (_uploadCount > 0) Log.Information("Glass CPU bitmap upload: {Count} frames, {Cost:0.00} ms/frame, {Deferrals} nonblocking deferrals (not GPU render time)", _uploadCount, _uploadMilliseconds / _uploadCount, _uploadDeferrals);
        _uploadCount = 0;
        _glass?.Dispose(); _glass = null; _glassScene.Clear(); _glassTexture = new();
        _captureBounds = Rect.Empty;
    }

    private void UpdateLensGeometry()
    {
        if (_closed || _surface.ActualWidth <= 0) return;
        UpdateGlassOptions(); UpdateCaptureMapping();
        var grid = _body.Children.OfType<UniformGrid>().FirstOrDefault();
        var buttons = grid?.Children.OfType<Button>().ToArray() ?? [];
        for (int i=0;i<15;i++)
        {
            Rect? r = null;
            if (i < buttons.Length && buttons[i].ActualWidth > 0)
            {
                var point = buttons[i].TranslatePoint(new Point(), _surface);
                r = new Rect(point, buttons[i].RenderSize);
            }
            _lens.SetSlot(i,r);
        }
        _lens.CommitSlots((int)Math.Ceiling(_surface.ActualWidth),(int)Math.Ceiling(_surface.ActualHeight));
        RefreshGlassPointer();
    }

    private void FocusFirstAction()
    {
        var first = _body.Children.OfType<UniformGrid>().FirstOrDefault()?.Children.OfType<Button>().FirstOrDefault(button => button.IsEnabled);
        (first ?? _settings).Focus();
    }

    private void StyleAction(Button button)
    {
        if (SystemParameters.HighContrast) return;
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.BorderBrush = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(94, 255, 255, 255), 0),
            new(Color.FromArgb(16, 255, 255, 255), .45),
            new(Color.FromArgb(18, 0, 0, 0), .8),
            new(Color.FromArgb(45, 255, 255, 255), 1),
        }, new Point(0, 0), new Point(1, 1));
        // Inner/outer shadows are part of the background pass; content stays unfiltered.
        var pressProperty = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(Button));
        EventHandler pressChanged = (_, _) => {
                var grid = _body.Children.OfType<UniformGrid>().FirstOrDefault();
                int index = grid?.Children.IndexOf(button) ?? -1;
                var pointer = _lens.Pointer;
                _lens.Pointer = new System.Windows.Media.Media3D.Point4D(pointer.X, pointer.Y, button.IsPressed ? 1 : 0, index+1);
            };
        button.Loaded += (_, _) => pressProperty.AddValueChanged(button, pressChanged);
        button.Unloaded += (_, _) => pressProperty.RemoveValueChanged(button, pressChanged);
    }

    private void DragPanel(object sender, MouseButtonEventArgs e)
    {
        if (!IsPanelDragSurface(e.OriginalSource as DependencyObject, _surface)) return;
        if (e.ButtonState != MouseButtonState.Pressed) return;
        e.Handled = true;
        _switching = true;
        try { DragMove(); }
        finally { _switching = false; }
    }

    internal static bool IsPanelDragSurface(DependencyObject? target, DependencyObject surface)
    {
        while (target != null)
        {
            // Preserve clicks, selection, popup editing, scrolling and thumb
            // gestures even when their original source is a nested text/path.
            if (target is ButtonBase or TextBoxBase or PasswordBox or Selector or ScrollBar or Thumb or System.Windows.Documents.Hyperlink)
                return false;
            if (ReferenceEquals(target,surface)) return true;
            target = target is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(target)
                : target is FrameworkContentElement content ? content.Parent
                : LogicalTreeHelper.GetParent(target);
        }
        return false;
    }

    private void LanguageChanged() { Title = Loc.T("Action panel"); Refresh(); }
    private void HistoryChanged() { if (IsVisible && _page == "clipboard") Refresh(); }

    public void Toggle()
    {
        if (IsVisible) { if (_previewingMaterial && !IsActive) { Activate(); FocusFirstAction(); } else Dismiss(); return; }
        _previous = GetForegroundWindow(); _page = "home";  _status.Text = "";
        GetCursorPos(out POINT cursor);
        var placement = PanelPlacement.AtCursor(MonitorInfo.GetAll(), cursor.X, cursor.Y, _actions.HomeHeight);
        Width = placement.WidthDip;
        _availableHeight = placement.AvailableHeightDip;
        Height = placement.HeightDip;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var bounds = placement.PixelBounds;
        SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        UpdateNativeBackdrop();
        Refresh();
        // Arrange all lenses before the first visible composition, not on a later Loaded callback.
        _root.Measure(new Size(Width, Height)); _root.Arrange(new Rect(0, 0, Width, Height));
        UpdateLensGeometry(); StartGlass();
        PopupPlacement.ShowActivated(this); UpdateLensGeometry(); _glass?.Start(); FocusFirstAction();
    }

    public void ShowMaterialPreview()
    {
        if (!IsVisible) Toggle();
        // Settings needs its preview to remain visible while sliders have focus.
        // This is temporary and cleared on Hide; there is no user pin toggle.
        _previewingMaterial = true;
    }

    private void Navigate(string page)
    {
        _translation?.Cancel(); _page = page;
        ResizeToPage(); _status.Text = ""; Refresh();
    }

    public void RefreshActions()
    {
        if (_closed || _page != "home") return;
        ResizeToPage();
        Refresh();
    }

    private void Refresh()
    {
        if (_closed) return;
        ResetGlassInteraction();
        _body.Children.Clear();
        _heading.Text = _page == "translate" ? Loc.T("Translation") : "Chao Desk";
        _home.Visibility = _page == "home" ? Visibility.Collapsed : Visibility.Visible;
        _home.ToolTip = Loc.Pick("Back", "返回"); AutomationProperties.SetName(_home,Loc.Pick("Back","返回"));
        _settings.Visibility = _page == "home" ? Visibility.Visible : Visibility.Collapsed;
        _settings.ToolTip = Loc.Pick("Panel settings", "面板设置"); AutomationProperties.SetName(_settings, Loc.Pick("Panel settings", "面板设置"));
        if (_page == "translate") BuildTranslation();
        else BuildActions();
        Dispatcher.BeginInvoke(UpdateLensGeometry, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Invoke(string id)
    {
        if (id == "translate") Navigate("translate");
        else RunExternal(id);
    }

    private void BuildTranslation()
    {
        var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 114, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 15, Padding = new Thickness(12), BorderThickness = new Thickness(0), Foreground = _ink };
        input.SetResourceReference(BackgroundProperty, "ControlBrush"); AutomationProperties.SetName(input, Loc.T("Text to translate"));
        try { if (Clipboard.ContainsText()) input.Text = Clipboard.GetText(); } catch (COMException) { }
        _body.Children.Add(input);
        _body.Children.Add(new TextBlock { Text = Loc.T("Translation sends this text to your selected translation service."), Foreground = _muted, Effect = GlyphContrast(), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 12) });
        var output = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 15, MinHeight = 100, MaxHeight = 150, Padding = new Thickness(12), BorderThickness = new Thickness(0), Foreground = _ink, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
        output.SetResourceReference(BackgroundProperty, "ControlBrush"); AutomationProperties.SetName(output, Loc.T("Translation result"));
        Button? translate = null;
        var copy = TextButton(Loc.T("Copy"), () => { try { Clipboard.SetDataObject(output.Text, true); _status.Text = Loc.T("Copied"); } catch (COMException) { _status.Text = Loc.T("Could not copy. Try again."); } });
        translate = TextButton(Loc.T("Translate"), () => _ = TranslateText());
        translate.Margin = new Thickness(0, 0, 0, 12); _body.Children.Add(translate); _body.Children.Add(output);
        copy.Visibility = Visibility.Collapsed; copy.Margin = new Thickness(0, 10, 0, 0); _body.Children.Add(copy);
        async Task TranslateText()
        {
            if (string.IsNullOrWhiteSpace(input.Text)) { _status.Text = Loc.T("Enter text to translate."); return; }
            _translation?.Cancel(); _translation?.Dispose(); var cts = _translation = new CancellationTokenSource();
            translate!.IsEnabled = false; _status.Text = Loc.T("Translating…");
            try { var result = await _translate(input.Text, cts.Token); if (cts.IsCancellationRequested) return; output.Text = result.Text; output.Visibility = copy.Visibility = Visibility.Visible; _status.Text = ""; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warning(ex, "Panel translation failed"); if (!cts.IsCancellationRequested) _status.Text = Loc.T("Could not reach a translation service. Check the connection and try again."); }
            finally { translate.IsEnabled = true; }
        }
    }

    private bool CanReturn() => _previous != IntPtr.Zero && IsWindow(_previous) && _previous != new WindowInteropHelper(this).Handle;

    private async Task ReturnAndRun(Action action)
    {
        if (!CanReturn()) return;
        var target = _previous; _switching = true;
        try
        {
            if (!_previewingMaterial) Hide();
            SetForegroundWindow(target); await Task.Delay(90); await KeyboardOutput.WaitForModifiersReleasedAsync();
            if (GetForegroundWindow() == target) action();
        }
        finally { _switching = false; }
    }

    private async void RunExternal(string id)
    {
        if (id == "restore")
        {
            string? path = CanReturn() ? GetWindowProcessPath(_previous) : null;
            string name = Path.GetFileNameWithoutExtension(path ?? "");
            if (!new[] { "msedge", "chrome", "firefox", "brave", "vivaldi", "opera", "arc" }.Contains(name, StringComparer.OrdinalIgnoreCase))
            { _status.Text = Loc.T("Open this panel over a browser to reopen its last closed tab."); return; }
            await ReturnAndRun(KeyboardOutput.SendCtrlShiftT); return;
        }
        _switching = true;
        try { Hide(); if (CanReturn()) SetForegroundWindow(_previous); await Task.Delay(180); _execute(id); }
        catch (Exception ex) { Log.Error(ex, "Panel action {Action} failed", id); }
        finally { _switching = false; }
    }

    private void Dismiss() => Dismiss(true);

    internal void HandlePointerPress(PanelMouseHook.PointerPress press)
    {
        if (!IsVisible || _closed || _previewingMaterial || _switching || _nativeMoving) return;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (press.TargetWindow != IntPtr.Zero && press.TargetWindow == hwnd) return;
        var point = new Point(press.X,press.Y);
        if (GetWindowRect(hwnd,out RECT r) && new Rect(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top).Contains(point)) return;
        // Deactivated is not raised when foreground activation was refused on
        // the initial Show. An observed outside press still closes the popup.
        Dismiss(false);
    }

    private void Dismiss(bool restore)
    {
        if (!IsVisible) return;
        _translation?.Cancel(); Hide(); if (restore && CanReturn()) SetForegroundWindow(_previous);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { if (_page != "home") Navigate("home"); else Dismiss(); e.Handled = true; }

    }

    private static readonly GlassGlyphEffect GlyphUnderlay = CreateGlyphUnderlay();
    private static GlassGlyphEffect CreateGlyphUnderlay()
    {
        // A tight glyph-sized underlay preserves white content on bright glass
        // without darkening whole buttons. Freeze and share its configuration.
        var effect = new GlassGlyphEffect();
        effect.Freeze(); return effect;
    }
    private static GlassGlyphEffect GlyphContrast() => GlyphUnderlay;
    private TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = _muted, Effect = GlyphContrast(), TextWrapping = TextWrapping.Wrap };
    private new ShapePath Icon(string data, double size)
    {
        var icon = new ShapePath { Data = Geometry.Parse(data), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Effect = GlyphContrast() };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "InkBrush");
        return icon;
    }
    private Button IconButton(string path, string name, Action run)
    {
        var button = Button(Icon(path, 18), run); button.Width = 34; button.Height = 34; button.Padding = new Thickness(7); button.ToolTip = name; AutomationProperties.SetName(button, name); return button;
    }
    private Button TextButton(string name, Action run)
    {
        var text = new TextBlock { Text = name, FontSize = 12, Effect = GlyphContrast(), VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(ForegroundProperty, "InkBrush");
        return Button(text, run);
    }
    private Button Button(object content, Action run)
    {
        var b = new Button { Content = content, Foreground = _ink, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(8, 6, 8, 6), Cursor = Cursors.Hand };
        b.SetResourceReference(ForegroundProperty, "InkBrush");
        var template = new ControlTemplate(typeof(Button));
        var layers = new FrameworkElementFactory(typeof(Grid));
        var optical = new FrameworkElementFactory(typeof(Border)); optical.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        optical.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        optical.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        optical.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        // Shadow only the transparent shell; rasterizing the whole button blurs its text/icons.
        optical.SetBinding(EffectProperty, new System.Windows.Data.Binding("Tag") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        layers.AppendChild(optical);
        var border = new FrameworkElementFactory(typeof(Border)); border.Name = "Surface"; border.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.HorizontalAlignmentProperty, new System.Windows.Data.Binding("HorizontalContentAlignment") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter); layers.AppendChild(border); template.VisualTree = layers;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Border.BackgroundProperty, _hover, "Surface")); template.Triggers.Add(hover);
        // Mouse focus is not an accent outline. Keyboard focus uses the standard focus adorner.
        var focusStyle = new Style(typeof(Control));
        var focusTemplate = new ControlTemplate(typeof(Control));
        var focusBorder = new FrameworkElementFactory(typeof(Border));
        focusBorder.SetValue(Border.CornerRadiusProperty,new CornerRadius(12));
        focusBorder.SetValue(Border.BorderThicknessProperty,new Thickness(1));
        focusBorder.SetValue(Border.BorderBrushProperty,new SolidColorBrush(Color.FromArgb(170,255,255,255)));
        focusTemplate.VisualTree=focusBorder; focusStyle.Setters.Add(new Setter(TemplateProperty,focusTemplate)); b.FocusVisualStyle=focusStyle;
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true }; pressed.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(30,255,255,255)), "Surface")); template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(OpacityProperty, .45)); template.Triggers.Add(disabled);
        b.Template = template; b.Click += (_, _) => run(); return b;
    }
}
