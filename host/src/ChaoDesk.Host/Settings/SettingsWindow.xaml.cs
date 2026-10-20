using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChaoDesk.Host.Files;
using ChaoDesk.Host.Input;
using ChaoDesk.Host.Library;
using Microsoft.Win32;
using ChaoDesk.Host.Localization;
using ChaoDesk.Host.Translate;

namespace ChaoDesk.Host.Settings;

/// <summary>What the settings window needs from the running app. Implemented by <see cref="AppHost"/>.</summary>
public interface ISettingsHost
{
    AppSettings Settings { get; }
    WallpaperLibrary Library { get; }
    string CurrentWallpaperId { get; }
    bool DevToolsAvailable { get; }
    Update.UpdateService Updates { get; }
    string StatusSummary { get; }
    Task ApplyWallpaperAsync(string id);
    JsonObject CurrentProperties();
    void SetProperty(string key, JsonNode value);
    void ResetProperties();
    void ApplyGeneralSettings();
    IReadOnlyList<(string Section, string Label, string Value)> DiagnosticsRows();
    void OpenLogFolder();
    Task ReloadAsync();
    void OpenDevTools();
    ImportResult Import(string path);
    IReadOnlyList<TaskItem> Tasks { get; }
    void AddTask(string text);
    void ToggleTask(string id);
    void RemoveTask(string id);
    void ClearDoneTasks();
    IReadOnlyList<WidgetItem> Widgets { get; }
    string WidgetLayout { get; set; }
    string CardDisplay { get; set; }
    int WidgetsPerPage { get; set; }
    void SetWidgetEnabled(string id, bool enabled);
    void MoveWidget(string id, int delta);
    IReadOnlyList<ShortcutItem> Shortcuts { get; }
    string? AddShortcut(string name, string target);
    void RemoveShortcut(string id);
    void MoveShortcut(string id, int delta);
    bool RemoveWallpaper(string id);
    // Launcher, clipboard, file search
    string HotkeyText(string which);
    string ScreenshotFolder { get; }
    void SetScreenshotFolder(string folder);
    int ScreenshotJpegQuality { get; set; }
    bool ScreenshotAutoSave { get; set; }
    string OcrLanguage { get; set; }
    bool OcrMergeLines { get; set; }
    bool OcrAutoTranslate { get; set; }
    bool OcrCopyDirectly { get; set; }
    bool QuickScreenshotSaves { get; set; }
    bool QuickLookEnabled { get; set; }
    string Theme { get; set; }
    string Language { get; set; }
    string TranslateEngine { get; set; }
    string TranslateTarget { get; set; }
    void OpenTool(string tool);
    string? HotkeyProblem(string which);
    string? SetHotkey(string which, string text);
    void SuspendHotkeys(bool suspend);
    bool ClipboardEnabled { get; set; }
    int ClipboardMaxItems { get; set; }
    string ClipboardIgnoredApps { get; set; }
    int ClipboardCount { get; }
    void ClearClipboardHistory();
    bool FileSearchEnabled { get; set; }
    bool FileSearchIncludeHidden { get; set; }
    bool FileSearchSkipJunk { get; set; }
    IReadOnlyList<string> FileSearchExcluded { get; }
    void SetFileSearchExcluded(IEnumerable<string> folders);
    FileIndexState FileSearchState { get; }
    string FileSearchStatus { get; }
    long FileIndexMemory { get; }
    Task SetUpFileSearchAsync();
    Task RemoveFileSearchAsync();
    event Action? StateChanged;
}

public partial class SettingsWindow : Window
{
    private static readonly string[] ColorPresets =
    [
        "#1c3d4a", "#16323f", "#10262e", "#1f4a4f", "#2a5a5c", "#37545e",
        "#243b55", "#2e3a4a", "#1b2a3a", "#2b4a3a", "#3a4a3f", "#4a5a44",
        "#3d3a33", "#4a3f3a", "#2a2a2e", "#5a6a70", "#6f8a86", "#8aa39b",
    ];

    private readonly ISettingsHost _host;
    private readonly DispatcherTimer _diagTimer;
    private bool _loading;

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
        InitializeComponent();
        VersionText.Text = Loc.F("Version {0}", typeof(App).Assembly.GetName().Version?.ToString(3));
        FillHelpTexts();
        using (var s = typeof(App).Assembly.GetManifestResourceStream("ChaoDesk.Host.Assets.chaodesk.ico"))
        {
            if (s != null)
            {
                var decoder = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                LogoImage.Source = frame;
                Icon = frame;
            }
        }
        DevToolsButton.Visibility = host.DevToolsAvailable ? Visibility.Visible : Visibility.Collapsed;

        _diagTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _diagTimer.Tick += (_, _) => { if (DiagnosticsPage.Visibility == Visibility.Visible) BuildDiagnostics(); };

        _host.StateChanged += OnHostStateChanged;
        _host.Updates.Changed += UpdateUpdatesRow;
        Closed += (_, _) =>
        {
            _host.StateChanged -= OnHostStateChanged;
            _host.Updates.Changed -= UpdateUpdatesRow;
            _diagTimer.Stop();
        };

        BuildLibrary();
        BuildProperties();
        BuildHome();
        LoadGeneral();
        UpdateUpdatesRow();
        LoadLauncher();
        LoadClipboard();
        _hotkeyErrors["launcher"] = LauncherHotkeyError;
        _hotkeyErrors["clipboard"] = ClipboardHotkeyError;
        _hotkeyBoxes.Add(LauncherHotkeyBox);
        _hotkeyBoxes.Add(ClipboardHotkeyBox);
        BuildScreenshotHotkeys();
        foreach (var box in _hotkeyBoxes) WireHotkeyBox(box);
        LoadScreenshot();
        UpdateStatus();
    }

    public void ShowPage(string page)
    {
        (page switch
        {
            "properties" => NavProperties,
            "tasks" => NavTasks,
            "widgets" => NavWidgets,
            "general" => NavGeneral,
            "diagnostics" => NavDiagnostics,
            "library" => NavLibrary,
            "clipboard" => NavClipboard,
            "screenshot" => NavScreenshot,
            "launcher" => NavLauncher,
            _ => NavHome,
        }).IsChecked = true;
    }

    private void OnHostStateChanged()
    {
        UpdateStatus();
        if (DiagnosticsPage.Visibility == Visibility.Visible) BuildDiagnostics();
        if (TasksPage.Visibility == Visibility.Visible) BuildTasks();
        if (LauncherPage.Visibility == Visibility.Visible) UpdateFileSearchStatus();
        if (HomePage.Visibility == Visibility.Visible) BuildHome();
        if (ClipboardPage.Visibility == Visibility.Visible) ClipboardCountText.Text = ClipboardCountLine();
    }

    private void UpdateStatus() => StatusText.Text = _host.StatusSummary;

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (LibraryPage == null) return; // during InitializeComponent
        HomePage.Visibility = NavHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavHome.IsChecked == true) BuildHome();
        LauncherPage.Visibility = NavLauncher.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ClipboardPage.Visibility = NavClipboard.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotPage.Visibility = NavScreenshot.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavScreenshot.IsChecked == true) LoadScreenshot();
        if (NavLauncher.IsChecked == true) LoadLauncher();
        if (NavClipboard.IsChecked == true) LoadClipboard();
        LibraryPage.Visibility = NavLibrary.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PropertiesPage.Visibility = NavProperties.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        GeneralPage.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TasksPage.Visibility = NavTasks.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        WidgetsPage.Visibility = NavWidgets.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavWidgets.IsChecked == true) BuildWidgets();
        if (NavTasks.IsChecked == true) { BuildTasks(); Dispatcher.BeginInvoke(() => NewTaskBox.Focus(), System.Windows.Threading.DispatcherPriority.Input); }
        DiagnosticsPage.Visibility = NavDiagnostics.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavDiagnostics.IsChecked == true) { BuildDiagnostics(); _diagTimer.Start(); } else _diagTimer.Stop();
        if (NavLibrary.IsChecked == true) BuildLibrary(); // refresh previews generated meanwhile
    }

    // ---- Library ------------------------------------------------------------------------------------

    private void BuildLibrary()
    {
        LibraryPanel.Children.Clear();
        foreach (var pkg in _host.Library.Packages) LibraryPanel.Children.Add(LibraryCard(pkg));
        if (_host.Library.Packages.Count == 0)
        {
            LibraryPanel.Children.Add(new TextBlock
            {
                Text = Loc.T("No wallpapers found. Import a wallpaper folder or .zip to get started."),
                Style = (Style)FindResource("PageSubtitle"),
            });
        }
    }

    private FrameworkElement LibraryCard(WallpaperPackage pkg)
    {
        bool active = pkg.Id.Equals(_host.CurrentWallpaperId, StringComparison.OrdinalIgnoreCase);
        var card = new Border
        {
            Width = 260,
            Margin = new Thickness(0, 0, 16, 16),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = active ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(active ? 2 : 1),
            Cursor = Cursors.Hand,
            ToolTip = pkg.IsBuiltIn ? Loc.T("Built-in wallpaper") : Loc.F("Imported · {0}", pkg.Folder),
        };

        var stack = new StackPanel();
        var previewHost = new Border
        {
            Height = 146,
            CornerRadius = new CornerRadius(5, 5, 0, 0),
            ClipToBounds = true,
            Background = new LinearGradientBrush(ParseColor(pkg.Manifest.Background), Darken(ParseColor(pkg.Manifest.Background), 0.6), 45),
        };
        var img = LoadPreview(pkg.PreviewPath);
        if (img != null)
        {
            previewHost.Child = new Image { Source = img, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        else
        {
            previewHost.Child = new TextBlock
            {
                Text = Loc.T("Preview is created the first time this wallpaper runs"),
                Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                FontSize = 11.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20),
            };
        }
        // Rounded top corners: clip the image with a geometry matching the border.
        previewHost.Loaded += (_, _) => previewHost.Clip = new RectangleGeometry(new Rect(0, 0, previewHost.ActualWidth, previewHost.ActualHeight + 6), 5, 5);
        stack.Children.Add(previewHost);

        var info = new Grid { Margin = new Thickness(14, 10, 14, 12) };
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock { Text = Loc.T(pkg.Manifest.Name), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 70, 0) });
        string byline = string.IsNullOrWhiteSpace(pkg.Manifest.Author) ? "" : Loc.F("by {0}", pkg.Manifest.Author);
        if (!string.IsNullOrWhiteSpace(pkg.Manifest.Version)) byline += (byline.Length > 0 ? " · " : "") + $"v{pkg.Manifest.Version}";
        texts.Children.Add(new TextBlock { Text = byline, FontSize = 12.5, Foreground = (Brush)FindResource("MutedBrush") });
        info.Children.Add(texts);
        if (active)
        {
            info.Children.Add(new Border
            {
                Background = (Brush)FindResource("AccentSoftBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = Loc.T("In use"), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("InkBrush") },
            });
        }
        stack.Children.Add(info);
        card.Child = stack;

        card.MouseEnter += (_, _) => { if (!active) card.BorderBrush = (Brush)FindResource("MutedBrush"); };
        card.MouseLeave += (_, _) => { if (!active) card.BorderBrush = (Brush)FindResource("LineBrush"); };
        card.MouseLeftButtonUp += async (_, _) =>
        {
            if (active) return;
            await _host.ApplyWallpaperAsync(pkg.Id);
            BuildLibrary();
            BuildProperties();
        };

        if (!pkg.IsBuiltIn)
        {
            var menu = new ContextMenu();
            var remove = new MenuItem { Header = Loc.T("Remove from library") };
            remove.Click += async (_, _) =>
            {
                if (MessageBox.Show(this, Loc.F("Remove \"{0}\" from your library?", Loc.T(pkg.Manifest.Name)), "Chao Desk",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                bool wasActive = active;
                if (_host.RemoveWallpaper(pkg.Id) && wasActive && _host.Library.Packages.Count > 0)
                    await _host.ApplyWallpaperAsync(_host.Library.Packages[0].Id);
                BuildLibrary();
                BuildProperties();
            };
            menu.Items.Add(remove);
            card.ContextMenu = menu;
        }
        return card;
    }

    private static BitmapImage? LoadPreview(string? path)
    {
        if (path == null) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // don't lock the file (previews get regenerated)
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 520;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void OnImportFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Choose a wallpaper folder (it must contain wallpaper.json)") };
        if (dlg.ShowDialog(this) == true) RunImport(dlg.FolderName);
    }

    private void OnImportZip(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = Loc.T("Choose a wallpaper .zip"), Filter = Loc.T("Wallpaper package") + " (*.zip)|*.zip" };
        if (dlg.ShowDialog(this) == true) RunImport(dlg.FileName);
    }

    private async void RunImport(string path)
    {
        var result = _host.Import(path);
        BuildLibrary();
        if (!result.Success)
        {
            MessageBox.Show(this, result.Message, Loc.T("Import wallpaper"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var answer = MessageBox.Show(this, result.Message + "\n\n" + Loc.T("Use it now?"), Loc.T("Import wallpaper"), MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes && result.Package != null)
        {
            await _host.ApplyWallpaperAsync(result.Package.Id);
            BuildLibrary();
            BuildProperties();
        }
    }

    // ---- Wallpaper properties ----------------------------------------------------------------------

    private void BuildProperties()
    {
        PropertiesPanel.Children.Clear();
        var pkg = _host.Library.Find(_host.CurrentWallpaperId);
        PropertiesTitle.Text = pkg != null ? Loc.T(pkg.Manifest.Name) : Loc.T("Wallpaper");
        if (pkg == null || pkg.Manifest.Properties.Count == 0)
        {
            PropertiesPanel.Children.Add(new TextBlock { Text = Loc.T("This wallpaper has no adjustable properties."), Margin = new Thickness(0, 12, 0, 12), Foreground = (Brush)FindResource("MutedBrush") });
            return;
        }
        var values = _host.CurrentProperties();
        bool first = true;
        foreach (var def in pkg.Manifest.Properties)
        {
            if (!first) PropertiesPanel.Children.Add(new Separator { Style = (Style)FindResource("RowSeparator") });
            first = false;
            PropertiesPanel.Children.Add(PropertyRow(def, values[def.Key] ?? def.Default));
        }
    }

    private FrameworkElement PropertyRow(PropertyDefinition def, JsonNode value)
    {
        var row = new Grid { Style = (Style)FindResource("SettingRow") };
        var labels = new StackPanel();
        labels.Children.Add(new TextBlock { Text = Loc.T(def.Label), Style = (Style)FindResource("SettingLabel") });
        row.Children.Add(labels);

        switch (def.Type)
        {
            case PropertyType.Slider:
            {
                double v = value.GetValue<double>();
                var slider = new Slider
                {
                    Minimum = def.Min, Maximum = def.Max, Value = v, Width = 170,
                    TickFrequency = def.Step ?? (def.Max - def.Min) / 100, IsSnapToTickEnabled = def.Step != null,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var label = new TextBlock { Text = FormatNumber(v), Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("MutedBrush") };
                slider.ValueChanged += (_, e) =>
                {
                    label.Text = FormatNumber(e.NewValue);
                    _host.SetProperty(def.Key, JsonValue.Create(e.NewValue)!);
                };
                var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0) };
                panel.Children.Add(slider);
                panel.Children.Add(label);
                row.Children.Add(panel);
                break;
            }
            case PropertyType.Bool:
            {
                var cb = new CheckBox { Style = (Style)FindResource("ToggleSwitch"), IsChecked = value.GetValue<bool>() };
                cb.Click += (_, _) => _host.SetProperty(def.Key, JsonValue.Create(cb.IsChecked == true)!);
                row.Children.Add(cb);
                break;
            }
            case PropertyType.Color:
                row.Children.Add(ColorEditor(def, value.GetValue<string>()));
                break;
            case PropertyType.Combo:
            {
                var options = def.Options;
                var combo = new ComboBox { Style = (Style)FindResource("FieldCombo"), ItemsSource = options.Select(Loc.T).ToList() };
                combo.SelectedIndex = options.ToList().IndexOf(value.GetValue<string>());
                combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) _host.SetProperty(def.Key, JsonValue.Create(options[combo.SelectedIndex])!); };
                row.Children.Add(combo);
                break;
            }
            case PropertyType.Text:
            {
                var tb = new TextBox { Style = (Style)FindResource("FieldText"), Text = value.GetValue<string>() };
                if (def.MaxLength is > 0) tb.MaxLength = def.MaxLength.Value;
                var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                debounce.Tick += (_, _) => { debounce.Stop(); _host.SetProperty(def.Key, JsonValue.Create(tb.Text)!); };
                tb.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
                row.Children.Add(tb);
                break;
            }
        }
        return row;
    }

    private FrameworkElement ColorEditor(PropertyDefinition def, string hex)
    {
        var swatch = new Button { Style = (Style)FindResource("SwatchButton"), Background = new SolidColorBrush(ParseColor(hex)), Width = 30, Height = 30, ToolTip = Loc.T("Choose a colour") };
        var text = new TextBox { Style = (Style)FindResource("FieldText"), Width = 96, Text = hex, Margin = new Thickness(8, 0, 0, 0), FontFamily = new FontFamily("Cascadia Mono, Consolas") };

        void Apply(string value, bool updateText)
        {
            if (!WallpaperManifest.IsHexColor(value)) return;
            swatch.Background = new SolidColorBrush(ParseColor(value));
            if (updateText && text.Text != value) text.Text = value;
            _host.SetProperty(def.Key, JsonValue.Create(value.ToLowerInvariant())!);
        }

        var grid = new UniformGrid { Columns = 6, Margin = new Thickness(8) };
        var popup = new Popup
        {
            PlacementTarget = swatch, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border
            {
                Background = Brushes.White, BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Child = grid,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.18 },
                Margin = new Thickness(6),
            },
        };
        foreach (string preset in ColorPresets)
        {
            var b = new Button { Style = (Style)FindResource("SwatchButton"), Background = new SolidColorBrush(ParseColor(preset)), ToolTip = preset };
            b.Click += (_, _) => { Apply(preset, updateText: true); popup.IsOpen = false; };
            grid.Children.Add(b);
        }
        swatch.Click += (_, _) => popup.IsOpen = true;
        text.TextChanged += (_, _) =>
        {
            string t = text.Text.Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            if (t.Length == 7) Apply(t, updateText: false);
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0) };
        panel.Children.Add(swatch);
        panel.Children.Add(text);
        panel.Children.Add(popup);
        return panel;
    }

    private void OnResetProperties(object sender, RoutedEventArgs e)
    {
        _host.ResetProperties();
        BuildProperties();
    }

    // ---- General ------------------------------------------------------------------------------------

    private void LoadGeneral()
    {
        _loading = true;
        var s = _host.Settings;
        FpsCombo.ItemsSource = new[] { "30 FPS", "60 FPS" };
        FpsCombo.SelectedIndex = s.FpsLimit == 60 ? 1 : 0;
        ScaleCombo.ItemsSource = new[] { "50 %", "75 %", "100 %" };
        ScaleCombo.SelectedIndex = Array.FindIndex(AppSettings.AllowedRenderScales, x => Math.Abs(x - s.RenderScale) < 0.001) is var i and >= 0 ? i : 2;
        QualityCombo.ItemsSource = new[] { Loc.T("Auto"), Loc.T("High"), Loc.T("Low") };
        QualityCombo.SelectedIndex = (int)s.Quality;
        PauseMaxCheck.IsChecked = s.PauseWhenMaximized;
        PauseBatteryCheck.IsChecked = s.PauseOnBattery;
        MouseCheck.IsChecked = s.MouseInteraction;
        StartupCheck.IsChecked = s.StartWithWindows;
        OnlineCheck.IsChecked = s.OnlineInfo;
        AutoUpdateCheck.IsChecked = s.CheckForUpdates;
        CityBox.Text = s.WeatherCity;
        WallpaperCheck.IsChecked = s.WallpaperEnabled;
        DesktopWidgetsCheck.IsChecked = s.DesktopWidgets;
        DesktopWidgetsCheck.IsEnabled = !s.WallpaperEnabled;
        ThemeCombo.ItemsSource = new[] { Loc.T("Same as Windows"), Loc.T("Light"), Loc.T("Dark") };
        ThemeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeManager.Modes, _host.Theme));
        // Each language in its own name, so it can be found whatever the window is in.
        LanguageCombo.ItemsSource = new[] { Loc.T("Same as Windows"), "English", "简体中文" };
        LanguageCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Loc.Modes, _host.Language));
        TranslateTargetCombo.ItemsSource = Translator.Targets.Select(t => t == "auto" ? Loc.T("Automatic (Chinese ↔ English)") : Translator.NativeName(t)).ToArray();
        TranslateTargetCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Translator.Targets, _host.TranslateTarget));
        TranslateEngineCombo.ItemsSource = new[] { Loc.T("Automatic"), "Google", "Microsoft Bing" };
        TranslateEngineCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Translator.Engines, _host.TranslateEngine));
        _loading = false;
    }

    private void OnGeneralChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _host.Settings;
        s.FpsLimit = FpsCombo.SelectedIndex == 1 ? 60 : 30;
        s.RenderScale = AppSettings.AllowedRenderScales[Math.Clamp(ScaleCombo.SelectedIndex, 0, 2)];
        s.Quality = (QualityMode)Math.Clamp(QualityCombo.SelectedIndex, 0, 2);
        s.PauseWhenMaximized = PauseMaxCheck.IsChecked == true;
        s.PauseOnBattery = PauseBatteryCheck.IsChecked == true;
        s.MouseInteraction = MouseCheck.IsChecked == true;
        s.StartWithWindows = StartupCheck.IsChecked == true;
        s.OnlineInfo = OnlineCheck.IsChecked == true;
        s.CheckForUpdates = AutoUpdateCheck.IsChecked == true;
        s.WeatherCity = CityBox.Text.Trim();
        s.WallpaperEnabled = WallpaperCheck.IsChecked == true;
        s.DesktopWidgets = DesktopWidgetsCheck.IsChecked == true;
        DesktopWidgetsCheck.IsEnabled = !s.WallpaperEnabled;
        _host.ApplyGeneralSettings();
    }

    private void UpdateUpdatesRow()
    {
        var u = _host.Updates;
        var latest = u.Latest;
        UpdateTitle.Text = $"Chao Desk {Update.UpdateService.Current}";
        UpdateStatusText.Text = u.State switch
        {
            Update.UpdateState.Checking => Loc.T("Checking for updates…"),
            Update.UpdateState.UpToDate => Loc.F("You have the latest version (checked {0:HH:mm}).", u.LastChecked),
            Update.UpdateState.Available => Loc.F("Version {0} is available ({1:0.0} MB).", latest!.Version, latest.InstallerSize / 1048576.0),
            Update.UpdateState.Downloading => Loc.F("Downloading version {0}… {1:P0}", latest!.Version, u.Progress),
            Update.UpdateState.Installing => Loc.T("Installing; Chao Desk restarts by itself in a moment."),
            Update.UpdateState.Failed => u.Error ?? Loc.T("The update failed."),
            _ => Loc.T("Updates come from the app's GitHub releases."),
        };
        UpdateButton.Content = u.State switch
        {
            Update.UpdateState.Available => Loc.T("Download and install"),
            Update.UpdateState.Downloading or Update.UpdateState.Installing => Loc.T("Installing…"),
            Update.UpdateState.Checking => Loc.T("Checking…"),
            _ => Loc.T("Check for updates"),
        };
        UpdateButton.IsEnabled = u.State is not (Update.UpdateState.Checking or Update.UpdateState.Downloading or Update.UpdateState.Installing);
        UpdateCancelButton.Visibility = u.State == Update.UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        bool notes = latest is { Notes.Length: > 0 } && u.State is (Update.UpdateState.Available or Update.UpdateState.Downloading);
        UpdateNotesText.Text = notes ? latest!.Notes : "";
        UpdateNotesText.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnUpdateButton(object sender, RoutedEventArgs e)
    {
        if (_host.Updates.State == Update.UpdateState.Available) await _host.Updates.InstallAsync();
        else await _host.Updates.CheckAsync();
    }

    private void OnCancelUpdate(object sender, RoutedEventArgs e) => _host.Updates.CancelDownload();

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedIndex < 0) return;
        string mode = Loc.Modes[LanguageCombo.SelectedIndex];
        Dispatcher.BeginInvoke(() => _host.Language = mode); // after the combo box has closed; the window reopens
    }

    private void OnTranslateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (TranslateTargetCombo.SelectedIndex >= 0) _host.TranslateTarget = Translator.Targets[TranslateTargetCombo.SelectedIndex];
        if (TranslateEngineCombo.SelectedIndex >= 0) _host.TranslateEngine = Translator.Engines[TranslateEngineCombo.SelectedIndex];
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedIndex < 0) return;
        string mode = ThemeManager.Modes[ThemeCombo.SelectedIndex];
        Dispatcher.BeginInvoke(() => _host.Theme = mode); // after the combo box has closed
    }

    private void OnCityKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) OnGeneralChanged(sender, e);
    }

    // ---- Home ------------------------------------------------------------------------------------------

    private void BuildHome()
    {
        HomePanel.Children.Clear();
        string Hk(string which) => _host.HotkeyText(which) is { Length: > 0 } k ? k : Loc.T("no hotkey");
        HomePanel.Children.Add(HomeCard(Loc.T("Launcher"), "\uE721", NavLauncher,
            Loc.F("{0} · apps, files, sums and commands", Hk("launcher")),
            _host.FileSearchStatus, (Loc.T("Open"), () => _host.OpenTool("launcher"))));
        HomePanel.Children.Add(HomeCard(Loc.T("Clipboard"), "\uE77F", NavClipboard,
            _host.ClipboardEnabled ? Loc.F("{0} · paste anything you copied", Hk("clipboard")) : Loc.T("Off"),
            ClipboardCountLine(), (Loc.T("Open"), () => _host.OpenTool("clipboard"))));
        HomePanel.Children.Add(HomeCard(Loc.T("Screenshot"), "\uE722", NavScreenshot,
            Loc.F("{0} capture · {1} whole screen · {2} pin", Hk("screenshot"), Hk("quickshot"), Hk("pin")),
            Loc.F("Scrolling capture {0} · saved to {1}", Hk("scrollshot"), _host.ScreenshotFolder), (Loc.T("Capture"), () => _host.OpenTool("screenshot"))));
        HomePanel.Children.Add(HomeCard(Loc.T("Text recognition"), "", NavScreenshot,
            Loc.F("{0} · read the text in any part of the screen, offline", Hk("ocr")),
            Loc.F("Languages: {0}", string.Join(Loc.Pick(", ", "；"), Ocr.OcrPacks.Installed.Select(p => Loc.T(p.Name)))),
            (Loc.T("Recognise"), () => _host.OpenTool("ocr"))));
        HomePanel.Children.Add(HomeCard(Loc.T("Quick Look"), "\uE8FF", NavLauncher,
            _host.QuickLookEnabled ? Loc.T("Space in Explorer previews the selected file") : Loc.T("Off"),
            Loc.T("Pictures, PDF, video, music, text, Office documents, folders"), null));
        HomePanel.Children.Add(HomeCard(Loc.T("Wallpaper"), "\uE771", NavGeneral,
            _host.Settings.WallpaperEnabled ? Loc.T("The lotus pond behind your icons")
                : _host.Settings.DesktopWidgets ? Loc.T("Off — your widgets stay on the Windows wallpaper") : Loc.T("Off — Windows' own wallpaper shows"),
            _host.StatusSummary, null));
        HomePanel.Children.Add(HomeCard(Loc.T("Appearance"), "\uE790", NavGeneral,
            _host.Theme switch { "light" => Loc.T("Light"), "dark" => Loc.T("Dark"), _ => Loc.T("Same as Windows") },
            Loc.T("Change it under General"), null));
    }

    /// <summary>A tool's row: icon, name, what it does and its status, then text links (its action and its settings).</summary>
    private Border HomeCard(string title, string glyph, RadioButton page, string line1, string line2, (string Label, Action Run)? action)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyphText = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20, Width = 24, Margin = new Thickness(0, 1, 18, 0), VerticalAlignment = VerticalAlignment.Top };
        glyphText.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        grid.Children.Add(glyphText);
        var texts = new StackPanel();
        Grid.SetColumn(texts, 1);
        texts.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        texts.Children.Add(new TextBlock { Text = line1, Style = (Style)FindResource("SettingHint"), Margin = new Thickness(0, 4, 0, 0) });
        texts.Children.Add(new TextBlock { Text = line2, Style = (Style)FindResource("SettingHint"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        grid.Children.Add(texts);
        var links = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(24, -3, 0, 0) };
        Grid.SetColumn(links, 2);
        if (action is { } a) links.Children.Add(HomeLink(a.Label, a.Run));
        links.Children.Add(HomeLink(Loc.T("Settings"), () => page.IsChecked = true));
        grid.Children.Add(links);
        var row = new Border { Padding = new Thickness(4, 16, 4, 16), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        row.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        return row;
    }

    private Button HomeLink(string text, Action run)
    {
        var link = new Button { Content = text, Style = (Style)FindResource("TextButton"), Margin = new Thickness(4, 0, 0, 0) };
        link.Click += (_, _) => run();
        return link;
    }

    // ---- Launcher and file search -----------------------------------------------------------------------

    private void LoadLauncher()
    {
        _loading = true;
        ShowHotkey(LauncherHotkeyBox);
        FileSearchCheck.IsChecked = _host.FileSearchEnabled;
        FileHiddenCheck.IsChecked = _host.FileSearchIncludeHidden;
        FileSkipJunkCheck.IsChecked = _host.FileSearchSkipJunk;
        QuickLookCheck.IsChecked = _host.QuickLookEnabled;
        BuildExcluded();
        UpdateFileSearchStatus();
        _loading = false;
    }

    private void UpdateFileSearchStatus()
    {
        var state = _host.FileSearchState;
        string memory = state is FileIndexState.Ready or FileIndexState.Indexing ? " · " + Loc.F("about {0:N0} MB of memory", _host.FileIndexMemory / (1024 * 1024)) : "";
        FileSearchStatusText.Text = _host.FileSearchStatus + memory;
        bool canSetUp = state is FileIndexState.NotSetUp or FileIndexState.NeedsRepair or FileIndexState.Failed;
        _fileSearchSetupRemoves = !canSetUp;
        FileSearchSetupButton.Content = state switch
        {
            FileIndexState.NotSetUp => Loc.T("Set up…"),
            FileIndexState.NeedsRepair or FileIndexState.Failed => Loc.T("Set up again…"),
            _ => Loc.T("Remove…"),
        };
        // "Remove" makes sense whenever file search was set up; "Set up" only while it is turned on.
        FileSearchSetupButton.Visibility = canSetUp && !_host.FileSearchEnabled || state == FileIndexState.Off ? Visibility.Collapsed : Visibility.Visible;
        FileSearchSetupButton.ToolTip = canSetUp
            ? Loc.T("Creates a scheduled task that reads the drives with administrator rights. Windows asks once.")
            : Loc.T("Deletes the scheduled task that reads the drives (asks for administrator permission).");
    }

    /// <summary>Whether the setup button currently removes file search (set with its text, so no text is compared).</summary>
    private bool _fileSearchSetupRemoves;

    private void OnFileSearchToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.FileSearchEnabled = FileSearchCheck.IsChecked == true;
        UpdateFileSearchStatus();
    }

    private async void OnFileSearchSetup(object sender, RoutedEventArgs e)
    {
        FileSearchSetupButton.IsEnabled = false;
        try
        {
            if (_fileSearchSetupRemoves)
            {
                if (MessageBox.Show(this, Loc.T("Turn file search off and remove its scheduled task? Windows asks for administrator permission."), "Chao Desk",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                await _host.RemoveFileSearchAsync();
            }
            else await _host.SetUpFileSearchAsync();
        }
        finally
        {
            FileSearchSetupButton.IsEnabled = true;
            LoadLauncher();
        }
    }

    private void OnQuickLookToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) _host.QuickLookEnabled = QuickLookCheck.IsChecked == true;
    }

    private void OnFileHiddenToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) _host.FileSearchIncludeHidden = FileHiddenCheck.IsChecked == true;
    }

    private void OnFileSkipJunkToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) _host.FileSearchSkipJunk = FileSkipJunkCheck.IsChecked == true;
    }

    private void BuildExcluded()
    {
        ExcludedPanel.Children.Clear();
        if (_host.FileSearchExcluded.Count == 0)
        {
            ExcludedPanel.Children.Add(new TextBlock { Text = Loc.T("None."), Style = (Style)FindResource("SettingHint") });
            return;
        }
        foreach (string folder in _host.FileSearchExcluded)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var remove = new Button { Content = Loc.T("Remove"), Style = (Style)FindResource("SecondaryButton"), Padding = new Thickness(10, 3, 10, 3) };
            remove.Click += (_, _) => { _host.SetFileSearchExcluded(_host.FileSearchExcluded.Where(f => f != folder).ToList()); BuildExcluded(); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock { Text = folder, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            ExcludedPanel.Children.Add(row);
        }
    }

    private void OnAddExcludedFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Choose a folder to leave out of file search") };
        if (dlg.ShowDialog(this) != true) return;
        _host.SetFileSearchExcluded(_host.FileSearchExcluded.Append(dlg.FolderName).ToList());
        BuildExcluded();
    }

    // ---- Hotkey boxes ----------------------------------------------------------------------------------

    /// <summary>A read-only text box that records the next key combination pressed in it.</summary>
    private void WireHotkeyBox(TextBox box)
    {
        box.IsReadOnly = true;
        box.IsReadOnlyCaretVisible = false;
        box.Cursor = Cursors.Hand;
        box.HorizontalContentAlignment = HorizontalAlignment.Center;
        box.GotKeyboardFocus += (_, _) => { _host.SuspendHotkeys(true); box.Text = Loc.T("Press keys…"); };
        box.LostKeyboardFocus += (_, _) => { _host.SuspendHotkeys(false); ShowHotkey(box); };
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            string which = (string)box.Tag;
            if (key is Key.Escape or Key.Tab) { LeaveHotkeyBox(); return; }
            if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            {
                box.Text = ModifierText(Keyboard.Modifiers) + "+…";
                return;
            }
            string text = key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None ? "" : new Hotkey(Keyboard.Modifiers, key).ToString();
            if (text.Length > 0 && !Hotkey.Parse(text).IsUsable) { box.Text = Loc.T("Add Ctrl, Alt, Shift or Win"); return; }
            string? error = _host.SetHotkey(which, text);
            LeaveHotkeyBox(); // the new hotkey takes effect once the box lets go of the keyboard
            ShowHotkeyError(which, error);
        };
    }

    private void LeaveHotkeyBox()
    {
        Keyboard.ClearFocus();
        (NavLauncher.IsChecked == true ? NavLauncher : NavClipboard.IsChecked == true ? NavClipboard : NavScreenshot).Focus();
    }

    private static string ModifierText(ModifierKeys mods) =>
        string.Join("+", new[] { (ModifierKeys.Control, "Ctrl"), (ModifierKeys.Alt, "Alt"), (ModifierKeys.Shift, "Shift"), (ModifierKeys.Windows, "Win") }
            .Where(m => mods.HasFlag(m.Item1)).Select(m => m.Item2));

    private void ShowHotkey(TextBox box)
    {
        string which = (string)box.Tag;
        string value = _host.HotkeyText(which);
        box.Text = value.Length == 0 ? Loc.T("Off") : value;
        ShowHotkeyError(which, _host.HotkeyProblem(which));
    }

    private void ShowHotkeyError(string which, string? error)
    {
        if (!_hotkeyErrors.TryGetValue(which, out var target)) return;
        target.Text = error ?? "";
        target.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Screenshot ------------------------------------------------------------------------------------

    private readonly Dictionary<string, TextBlock> _hotkeyErrors = new();
    private readonly List<TextBox> _hotkeyBoxes = new();

    private void BuildScreenshotHotkeys()
    {
        var rows = new (string Which, string Label, string Hint)[]
        {
            ("screenshot", Loc.T("Screenshot"), Loc.T("Choose an area or a window, mark it up, then copy, save or pin it.")),
            ("quickshot", Loc.T("Quick screenshot"), Loc.T("The whole screen, straight to the clipboard.")),
            ("scrollshot", Loc.T("Scrolling screenshot"), Loc.T("Scrolls a page and stitches one long image. Esc stops.")),
            ("pin", Loc.T("Pin an area"), Loc.T("Choose an area; it stays on top of everything. Hover it to draw on it.")),
            ("pinstoggle", Loc.T("Hide / show pinned images"), Loc.T("Hide every pinned image, and bring them back.")),
            ("ocr", Loc.T("Recognise text"), Loc.T("Choose an area and read its text, offline. Also T while capturing.")),
        };
        bool first = true;
        foreach (var (which, label, hint) in rows)
        {
            if (!first) ScreenshotHotkeysPanel.Children.Add(new Separator { Style = (Style)FindResource("RowSeparator") });
            first = false;
            var grid = new Grid { Style = (Style)FindResource("SettingRow") };
            var texts = new StackPanel();
            texts.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("SettingLabel") });
            texts.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("SettingHint") });
            var error = new TextBlock { Style = (Style)FindResource("SettingHint"), Foreground = (Brush)FindResource("DangerBrush"), Visibility = Visibility.Collapsed };
            texts.Children.Add(error);
            grid.Children.Add(texts);
            var box = new TextBox { Style = (Style)FindResource("FieldText"), Width = 160, Tag = which };
            grid.Children.Add(box);
            ScreenshotHotkeysPanel.Children.Add(grid);
            _hotkeyErrors[which] = error;
            _hotkeyBoxes.Add(box);
        }
    }

    /// <summary>In the order of <see cref="AppSettings.AllowedJpegQualities"/> (100 = PNG).</summary>
    private static string[] ShotFormats =>
    [
        Loc.T("PNG — lossless"), Loc.F("JPEG — quality {0}", 95), Loc.F("JPEG — quality {0}", 90),
        Loc.F("JPEG — quality {0}", 80), Loc.F("JPEG — quality {0}", 70), Loc.F("JPEG — quality {0}", 60),
    ];

    private void LoadScreenshot()
    {
        _loading = true;
        foreach (var box in _hotkeyBoxes.Where(b => b != LauncherHotkeyBox && b != ClipboardHotkeyBox)) ShowHotkey(box);
        ShotFormatCombo.ItemsSource = ShotFormats;
        ShotFormatCombo.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.AllowedJpegQualities, _host.ScreenshotJpegQuality));
        ShotFolderText.Text = _host.ScreenshotFolder;
        ShotAutoSaveCheck.IsChecked = _host.ScreenshotAutoSave;
        ShotQuickSaveCheck.IsChecked = _host.QuickScreenshotSaves;
        OcrAfterCombo.ItemsSource = new[] { Loc.T("Show the text window"), Loc.T("Copy the text at once") };
        OcrAfterCombo.SelectedIndex = _host.OcrCopyDirectly ? 1 : 0;
        OcrMergeCheck.IsChecked = _host.OcrMergeLines;
        OcrTranslateCheck.IsChecked = _host.OcrAutoTranslate;
        LoadOcrLanguages();
        BuildOcrPacks();
        _loading = false;
    }

    private string[] _ocrTags = ["auto"];

    private void OnOcrChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (OcrLanguageCombo.SelectedIndex >= 0) _host.OcrLanguage = _ocrTags[OcrLanguageCombo.SelectedIndex];
        _host.OcrCopyDirectly = OcrAfterCombo.SelectedIndex == 1;
        _host.OcrMergeLines = OcrMergeCheck.IsChecked == true;
        _host.OcrAutoTranslate = OcrTranslateCheck.IsChecked == true;
    }

    /// <summary>"All installed languages", then each installed pack alone.</summary>
    private void LoadOcrLanguages()
    {
        bool was = _loading;
        _loading = true;
        var packs = Ocr.OcrPacks.Installed;
        _ocrTags = ["auto", .. packs.Select(p => p.Id)];
        OcrLanguageCombo.ItemsSource = new[] { packs.Count > 1 ? Loc.T("All installed languages") : Loc.T(Ocr.OcrPacks.BuiltIn.Name) }.Concat(packs.Count > 1 ? packs.Select(p => Loc.F("{0} only", Loc.T(p.Name))) : []).ToList();
        int index = Array.IndexOf(_ocrTags, _host.OcrLanguage);
        OcrLanguageCombo.SelectedIndex = index > 0 && packs.Count > 1 ? index : 0;
        _loading = was;
    }

    // ---- OCR language packs ----

    private readonly Dictionary<string, (CancellationTokenSource Cts, double Progress)> _packDownloads = new();
    private readonly Dictionary<string, string> _packErrors = new();

    /// <summary>One row per pack: what it reads, its size, and Add / Remove (or the download's progress).</summary>
    private void BuildOcrPacks()
    {
        OcrPacksPanel.Children.Clear();
        bool first = true;
        foreach (var pack in Ocr.OcrPacks.All)
        {
            if (!first) OcrPacksPanel.Children.Add(new Separator { Style = (Style)FindResource("RowSeparator") });
            first = false;
            var grid = new Grid { Style = (Style)FindResource("SettingRow") };
            var texts = new StackPanel();
            texts.Children.Add(new TextBlock { Text = Loc.T(pack.Name), Style = (Style)FindResource("SettingLabel") });
            texts.Children.Add(new TextBlock { Text = $"{Loc.T(pack.Covers)} · {pack.Size / 1_048_576.0:0.#} MB", Style = (Style)FindResource("SettingHint") });
            if (_packErrors.TryGetValue(pack.Id, out var error))
                texts.Children.Add(new TextBlock { Text = error, Style = (Style)FindResource("SettingHint"), Foreground = (Brush)FindResource("DangerBrush") });
            grid.Children.Add(texts);
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0) };
            if (pack.Id == Ocr.OcrPacks.BuiltInId) right.Children.Add(PackNote(Loc.T("Built in")));
            else if (_packDownloads.TryGetValue(pack.Id, out var download))
            {
                right.Children.Add(PackNote(Loc.F("Downloading {0:P0}", download.Progress)));
                right.Children.Add(PackLink(Loc.T("Cancel"), () => download.Cts.Cancel()));
            }
            else if (Ocr.OcrPacks.IsInstalled(pack))
            {
                right.Children.Add(PackNote(Loc.T("Installed")));
                right.Children.Add(PackLink(Loc.T("Remove"), () => RemovePack(pack)));
            }
            else right.Children.Add(PackLink(Loc.T("Add"), () => _ = AddPackAsync(pack)));
            grid.Children.Add(right);
            OcrPacksPanel.Children.Add(grid);
        }
    }

    private TextBlock PackNote(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }

    private Button PackLink(string text, Action action)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("TextButton"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => action();
        return b;
    }

    private async Task AddPackAsync(Ocr.OcrPack pack)
    {
        var cts = new CancellationTokenSource();
        _packDownloads[pack.Id] = (cts, 0);
        _packErrors.Remove(pack.Id);
        BuildOcrPacks();
        var progress = new Progress<double>(p =>
        {
            if (!_packDownloads.ContainsKey(pack.Id) || p - _packDownloads[pack.Id].Progress < 0.01 && p < 1) return;
            _packDownloads[pack.Id] = (cts, p);
            BuildOcrPacks();
        });
        try { await Ocr.OcrPacks.InstallAsync(pack, progress, cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException)
        {
            _packErrors[pack.Id] = Loc.F("Download failed: {0}", ex.Message);
        }
        _packDownloads.Remove(pack.Id);
        BuildOcrPacks();
        LoadOcrLanguages();
    }

    private void RemovePack(Ocr.OcrPack pack)
    {
        try { Ocr.OcrPacks.Remove(pack); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { _packErrors[pack.Id] = Loc.F("Could not remove it: {0}", ex.Message); }
        if (_host.OcrLanguage == pack.Id) _host.OcrLanguage = "auto";
        BuildOcrPacks();
        LoadOcrLanguages();
    }

    private void OnShotFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ShotFormatCombo.SelectedIndex < 0) return;
        _host.ScreenshotJpegQuality = AppSettings.AllowedJpegQualities[ShotFormatCombo.SelectedIndex];
    }

    private void OnShotTogglesChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.ScreenshotAutoSave = ShotAutoSaveCheck.IsChecked == true;
        _host.QuickScreenshotSaves = ShotQuickSaveCheck.IsChecked == true;
    }

    private void OnChangeShotFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Choose where screenshots are saved"), InitialDirectory = _host.ScreenshotFolder };
        if (dlg.ShowDialog(this) != true) return;
        _host.SetScreenshotFolder(dlg.FolderName);
        ShotFolderText.Text = _host.ScreenshotFolder;
    }

    private void OnOpenShotFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_host.ScreenshotFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_host.ScreenshotFolder}\"") { UseShellExecute = true });
    }

    // ---- Clipboard -------------------------------------------------------------------------------------

    private void LoadClipboard()
    {
        _loading = true;
        ClipboardCheck.IsChecked = _host.ClipboardEnabled;
        ShowHotkey(ClipboardHotkeyBox);
        ClipboardSizeCombo.ItemsSource = Clips.ClipboardHistory.AllowedSizes.Select(n => Loc.F("{0:N0} entries", n)).ToList();
        ClipboardSizeCombo.SelectedIndex = Array.IndexOf(Clips.ClipboardHistory.AllowedSizes, _host.ClipboardMaxItems);
        IgnoredAppsBox.Text = _host.ClipboardIgnoredApps;
        ClipboardCountText.Text = ClipboardCountLine();
        _loading = false;
    }

    private string ClipboardCountLine() => _host.ClipboardCount == 1 ? Loc.T("1 entry.") : Loc.F("{0:N0} entries.", _host.ClipboardCount);

    private void OnClipboardToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) _host.ClipboardEnabled = ClipboardCheck.IsChecked == true;
    }

    private void OnClipboardSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ClipboardSizeCombo.SelectedIndex < 0) return;
        _host.ClipboardMaxItems = Clips.ClipboardHistory.AllowedSizes[ClipboardSizeCombo.SelectedIndex];
        ClipboardCountText.Text = ClipboardCountLine();
    }

    private void OnIgnoredAppsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.ClipboardIgnoredApps = IgnoredAppsBox.Text;
        IgnoredAppsBox.Text = _host.ClipboardIgnoredApps;
    }

    private void OnIgnoredAppsKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnIgnoredAppsChanged(sender, e);
    }

    private void OnClearClipboard(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, Loc.T("Delete the clipboard history? Pinned entries are kept."), "Chao Desk", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _host.ClearClipboardHistory();
        ClipboardCountText.Text = ClipboardCountLine();
    }

    // ---- Widgets ------------------------------------------------------------------------------------

    /// <summary>In the order of <see cref="WidgetCatalog.Layouts"/> and <see cref="WidgetCatalog.Displays"/>.</summary>
    private static string[] LayoutNames => [Loc.T("Stacked (pages when full)"), Loc.T("Tabs")];
    private static string[] DisplayNames => [Loc.T("Clock until clicked"), Loc.T("Always open")];

    private void OnPerPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PerPageCombo.SelectedIndex < 0) return;
        _host.WidgetsPerPage = WidgetCatalog.PerPageOptions[PerPageCombo.SelectedIndex];
    }

    private void OnDisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DisplayCombo.SelectedIndex < 0) return;
        _host.CardDisplay = WidgetCatalog.Displays[DisplayCombo.SelectedIndex];
    }

    private void BuildWidgets()
    {
        _loading = true;
        LayoutCombo.ItemsSource = LayoutNames;
        LayoutCombo.SelectedIndex = Math.Max(0, Array.IndexOf(WidgetCatalog.Layouts, _host.WidgetLayout));
        PerPageCombo.ItemsSource = WidgetCatalog.PerPageOptions.Select(n => n == 0 ? Loc.T("Auto (fill the screen)") : Loc.F("{0} widgets", n)).ToArray();
        PerPageCombo.SelectedIndex = Math.Max(0, Array.IndexOf(WidgetCatalog.PerPageOptions, _host.WidgetsPerPage));
        PerPageCombo.IsEnabled = _host.WidgetLayout == "stack";
        DisplayCombo.ItemsSource = DisplayNames;
        DisplayCombo.SelectedIndex = Math.Max(0, Array.IndexOf(WidgetCatalog.Displays, _host.CardDisplay));
        _loading = false;

        WidgetsPanel.Children.Clear();
        var list = _host.Widgets;
        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            var info = WidgetCatalog.Find(item.Id);
            if (info == null) continue;
            if (WidgetsPanel.Children.Count > 0) WidgetsPanel.Children.Add(new Separator { Style = (Style)FindResource("RowSeparator") });
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var toggle = new CheckBox { Style = (Style)FindResource("ToggleSwitch"), IsChecked = item.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            string id = item.Id;
            toggle.Click += (_, _) => { _host.SetWidgetEnabled(id, toggle.IsChecked == true); BuildWidgets(); };
            DockPanel.SetDock(toggle, Dock.Right);
            row.Children.Add(toggle);
            var down = ArrowButton("▼", Loc.T("Move down"), i < list.Count - 1, () => { _host.MoveWidget(id, 1); BuildWidgets(); });
            var up = ArrowButton("▲", Loc.T("Move up"), i > 0, () => { _host.MoveWidget(id, -1); BuildWidgets(); });
            DockPanel.SetDock(down, Dock.Right); DockPanel.SetDock(up, Dock.Right);
            row.Children.Add(down); row.Children.Add(up);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Opacity = item.Enabled ? 1 : 0.6 };
            text.Children.Add(new TextBlock { Text = Loc.T(info.Name), Style = (Style)FindResource("SettingLabel") });
            text.Children.Add(new TextBlock { Text = Loc.T(info.Description), Style = (Style)FindResource("SettingHint"), TextWrapping = TextWrapping.Wrap });
            row.Children.Add(text);
            WidgetsPanel.Children.Add(row);
        }
        BuildShortcuts();
    }

    private Button ArrowButton(string glyph, string tip, bool enabled, Action onClick)
    {
        var b = new Button { Style = (Style)FindResource("SecondaryButton"), Content = glyph, ToolTip = tip, IsEnabled = enabled,
            Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void OnLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LayoutCombo.SelectedIndex < 0) return;
        _host.WidgetLayout = WidgetCatalog.Layouts[LayoutCombo.SelectedIndex];
        PerPageCombo.IsEnabled = _host.WidgetLayout == "stack";
    }

    private void BuildShortcuts()
    {
        ShortcutsPanel.Children.Clear();
        var list = _host.Shortcuts;
        if (list.Count == 0)
        {
            ShortcutsPanel.Children.Add(new TextBlock { Text = Loc.T("No shortcuts yet."), Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(2, 4, 0, 4) });
            return;
        }
        for (int i = 0; i < list.Count; i++)
        {
            var x = list[i];
            string id = x.Id;
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var del = new Button { Style = (Style)FindResource("SecondaryButton"), Content = Loc.T("Delete"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            del.Click += (_, _) => { _host.RemoveShortcut(id); BuildShortcuts(); };
            var down = ArrowButton("▼", Loc.T("Move down"), i < list.Count - 1, () => { _host.MoveShortcut(id, 1); BuildShortcuts(); });
            var up = ArrowButton("▲", Loc.T("Move up"), i > 0, () => { _host.MoveShortcut(id, -1); BuildShortcuts(); });
            foreach (var b in new FrameworkElement[] { del, down, up }) { DockPanel.SetDock(b, Dock.Right); row.Children.Add(b); }
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = x.Name, FontSize = 14 });
            text.Children.Add(new TextBlock { Text = x.Target, Style = (Style)FindResource("SettingHint"), TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(text);
            ShortcutsPanel.Children.Add(row);
        }
    }

    private void OnAddShortcut(object sender, RoutedEventArgs e)
    {
        string? error = _host.AddShortcut(ShortcutNameBox.Text, ShortcutTargetBox.Text);
        ShortcutError.Text = error ?? "";
        ShortcutError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        if (error != null) return;
        ShortcutNameBox.Clear();
        ShortcutTargetBox.Clear();
        BuildShortcuts();
    }

    private void OnShortcutKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) OnAddShortcut(sender, e);
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Choose a folder for the shortcut") };
        if (dlg.ShowDialog(this) == true) ShortcutTargetBox.Text = dlg.FolderName;
    }

    private void OnBrowseProgram(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("Choose a program or file"), Filter = Loc.T("Programs and shortcuts") + "|*.exe;*.lnk;*.bat;*.cmd|" + Loc.T("All files") + "|*.*", DereferenceLinks = false };
        if (dlg.ShowDialog(this) == true) ShortcutTargetBox.Text = dlg.FileName;
    }

    // ---- Tasks --------------------------------------------------------------------------------------

    private void BuildTasks()
    {
        TasksPanel.Children.Clear();
        var tasks = _host.Tasks;
        if (tasks.Count == 0)
        {
            TasksPanel.Children.Add(new TextBlock { Text = Loc.T("No tasks yet. Type one above and press Enter."), Margin = new Thickness(0, 12, 0, 12), Foreground = (Brush)FindResource("MutedBrush") });
            return;
        }
        bool first = true;
        foreach (var t in tasks.OrderBy(t => t.Done).ThenByDescending(t => t.Created))
        {
            if (!first) TasksPanel.Children.Add(new Separator { Style = (Style)FindResource("RowSeparator") });
            first = false;
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
            var del = new Button { Style = (Style)FindResource("SecondaryButton"), Content = Loc.T("Delete"), Padding = new Thickness(10, 3, 10, 3) };
            string id = t.Id;
            del.Click += (_, _) => { _host.RemoveTask(id); BuildTasks(); };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            var cb = new CheckBox { IsChecked = t.Done, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            cb.Click += (_, _) => { _host.ToggleTask(id); BuildTasks(); };
            row.Children.Add(cb);
            row.Children.Add(new TextBlock { Text = t.Text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                Foreground = t.Done ? (Brush)FindResource("MutedBrush") : (Brush)FindResource("InkBrush"), TextDecorations = t.Done ? TextDecorations.Strikethrough : null });
            TasksPanel.Children.Add(row);
        }
    }

    private void AddTaskFromBox()
    {
        string text = NewTaskBox.Text.Trim();
        if (text.Length == 0) return;
        _host.AddTask(text);
        NewTaskBox.Clear();
        BuildTasks();
    }

    private void OnAddTask(object sender, RoutedEventArgs e) => AddTaskFromBox();
    private void OnNewTaskKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { AddTaskFromBox(); e.Handled = true; } }
    private void OnClearDone(object sender, RoutedEventArgs e) { _host.ClearDoneTasks(); BuildTasks(); }

    // ---- Diagnostics --------------------------------------------------------------------------------

    private void BuildDiagnostics()
    {
        DiagnosticsPanel.Children.Clear();
        string? section = null;
        foreach (var (sec, label, value) in _host.DiagnosticsRows())
        {
            if (sec != section)
            {
                section = sec;
                DiagnosticsPanel.Children.Add(new TextBlock
                {
                    Text = sec, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("MutedBrush"),
                    Margin = new Thickness(0, DiagnosticsPanel.Children.Count == 0 ? 8 : 18, 0, 6),
                });
            }
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, Foreground = (Brush)FindResource("MutedBrush") });
            var v = new TextBox
            {
                Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0),
            };
            Grid.SetColumn(v, 1);
            row.Children.Add(v);
            DiagnosticsPanel.Children.Add(row);
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => _host.OpenLogFolder();
    private async void OnReload(object sender, RoutedEventArgs e) => await _host.ReloadAsync();
    private void OnOpenDevTools(object sender, RoutedEventArgs e) => _host.OpenDevTools();

    // ---- Helpers ------------------------------------------------------------------------------------

    /// <summary>The two help cards: one table entry per line, **bold** and `code` marked inside the text.</summary>
    private void FillHelpTexts()
    {
        SetMarkedLines(SearchHelpText,
            Loc.T("**Apps** — type part of the name, its initials or pinyin: `wx`, `weixin` and `微信` all find 微信; `vsc` finds Visual Studio Code."),
            Loc.T("**Files** — all words must be in the name. `ext:mp4;mkv` or `*.pdf` filter by type, `folder:` / `file:` by kind, and a word with a backslash (`work\\report`) matches the whole path."),
            Loc.T("**Also** — sums like `12*(3+4)`, web addresses, and Chao Desk commands (settings, clipboard, lock, exit)."),
            Loc.T("**Keys** — Tab switches All / Apps / Files · Enter opens · Ctrl+Enter shows it in its folder · Ctrl+Shift+Enter runs as administrator · Ctrl+Shift+C copies the path · Alt+1…9 opens a result."));
        SetMarkedLines(CaptureHelpText,
            Loc.T("Hover to pick a window or panel and click to take it, or drag any area. Drag the handles to adjust, arrow keys move it by one pixel (Shift+arrows resize)."),
            Loc.T("Toolbar: rectangle, ellipse, arrow, pen, highlighter, text, mosaic, numbered markers (Shift keeps squares and 45° lines) · Ctrl+Z undo."),
            _host.HotkeyText("pin") is { Length: > 0 } pin
                ? Loc.F("Enter, Ctrl+C or a double-click copies · Ctrl+S saves · Ctrl+T or {0} pins it to the screen · T recognises the text · C copies the colour under the mouse · right-click or Esc cancels.", pin)
                : Loc.T("Enter, Ctrl+C or a double-click copies · Ctrl+S saves · Ctrl+T pins it to the screen · T recognises the text · C copies the colour under the mouse · right-click or Esc cancels."),
            Loc.T("Pinned images: drag to move, wheel to zoom, Ctrl+wheel for opacity, double-click or Esc to close, right-click for more."));
    }

    private static void SetMarkedLines(TextBlock target, params string[] lines)
    {
        target.Inlines.Clear();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) target.Inlines.Add(new System.Windows.Documents.LineBreak());
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(lines[i], @"\*\*(.+?)\*\*|`([^`]+)`|[^*`]+|[*`]"))
            {
                var run = new System.Windows.Documents.Run(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Value);
                if (m.Groups[1].Success) run.FontWeight = FontWeights.SemiBold;
                if (m.Groups[2].Success) run.FontFamily = new FontFamily("Consolas");
                target.Inlines.Add(run);
            }
        }
    }

    private static string FormatNumber(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-9 ? ((long)Math.Round(v)).ToString(CultureInfo.CurrentCulture) : v.ToString("0.##", CultureInfo.CurrentCulture);

    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.Black; }
    }

    private static Color Darken(Color c, double f) =>
        Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
}
