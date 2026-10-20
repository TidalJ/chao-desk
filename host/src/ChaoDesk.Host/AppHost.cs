using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChaoDesk.Host.Capture;
using ChaoDesk.Host.Clips;
using ChaoDesk.Host.Desktop;
using ChaoDesk.Host.Files;
using ChaoDesk.Host.Input;
using ChaoDesk.Host.Launcher;
using ChaoDesk.Host.Preview;
using ChaoDesk.Host.Library;
using ChaoDesk.Host.Localization;
using ChaoDesk.Host.Ocr;
using ChaoDesk.Host.Online;
using ChaoDesk.Host.Power;
using ChaoDesk.Host.Rendering;
using ChaoDesk.Host.Settings;
using ChaoDesk.Host.Translate;
using ChaoDesk.Host.Tray;
using ChaoDesk.Host.Update;
using Microsoft.Win32;
using Serilog;

namespace ChaoDesk.Host;

/// <summary>Wires the host components together. Lives on the UI thread for the lifetime of the app.</summary>
public sealed class AppHost : ISettingsHost, IDisposable
{
    private readonly bool _devTools;
    private readonly Action _requestExit;
    private readonly AppSettings _settings;
    private readonly WallpaperLibrary _library;
    private readonly MessageWindow _messageWindow;
    private readonly WallpaperManager _manager;
    private readonly TrayIcon _tray;
    private readonly ExplorerWatcher _explorer;
    private readonly PausePolicy _pausePolicy;
    private readonly DispatcherTimer _displayDebounce;
    private readonly DispatcherTimer _saveDebounce;
    private readonly DispatcherTimer _previewTimer;
    private readonly OnlineInfoService _online;
    private readonly UpdateService _updates;
    private readonly SystemMonitor _system = new();
    private readonly DispatcherTimer _systemTick;
    private readonly Dictionary<string, string> _pageStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly HotkeyManager _hotkeys;
    private readonly Dictionary<string, int> _hotkeyIds = new();
    private readonly Dictionary<string, string> _hotkeyProblems = new();
    private readonly AppCatalog _catalog = new() { CachePath = System.IO.Path.Combine(AppPaths.Cache, "apps.bin") };
    private readonly FileIndexService _files = new();
    private readonly LaunchStats _stats;
    private readonly DispatcherTimer _statsSave;
    private readonly ClipboardHistory _clipHistory;
    private readonly ClipboardMonitor _clipMonitor;
    private readonly DispatcherTimer _clipSave;
    private readonly ImageSource? _appIcon;
    private LauncherWindow? _launcher;
    private ClipboardWindow? _clipWindow;
    private bool _hotkeysSuspended;
    private readonly ScreenshotService _screenshots;
    private QuickLookHook? _quickLookHook;
    private QuickLookWindow? _quickLook;
    private bool _wallpaperRunning; // the desktop layer: the pond, or only the widgets (_widgetsOnly)
    private bool _widgetsOnly;
    private SettingsWindow? _settingsWindow;
    private WallpaperPackage? _current;
    private bool _disposed;

    public event Action? StateChanged;

    public AppHost(bool devTools, Action requestExit)
    {
        _devTools = devTools;
        _requestExit = requestExit;
        _settings = AppSettings.Load(AppPaths.SettingsFile);
        ThemeManager.Apply(_settings.Theme);
        Loc.Apply(_settings.Language);
        // The Run key is the source of truth for "Start with Windows": the installer's option writes it,
        // and users can remove it in Task Manager > Startup apps.
        _settings.StartWithWindows = StartupRegistration.IsEnabled();
        _library = new WallpaperLibrary(AppPaths.BuiltInWallpapers, AppPaths.UserWallpapers, includeDiagnostic: devTools);
        _library.Scan();

        _messageWindow = new MessageWindow();
        _appIcon = LoadAppIcon();

        _manager = new WallpaperManager(devTools) { InitJsonFactory = BuildInitJson };
        _manager.StateChanged += OnManagerStateChanged;
        _manager.PageMessage += OnPageMessage;

        _tray = new TrayIcon(showSettings: true, showDevTools: devTools);
        _tray.LauncherRequested += ToggleLauncher;
        _tray.ClipboardRequested += ToggleClipboard;
        _tray.SettingsRequested += () => ShowSettings();
        _tray.AddTaskRequested += () => ShowSettings("tasks");
        _tray.PauseToggled += () => { _manager.SetUserPaused(!_manager.IsUserPaused); OnManagerStateChanged(); };
        _tray.DevToolsRequested += OpenDevTools;
        _tray.ExitRequested += _requestExit;
        _tray.Localize();

        _explorer = new ExplorerWatcher(_messageWindow, () => _manager.IsTargetLost);
        _explorer.ShellRestarted += () => _ = _manager.RebuildAsync("explorer restarted");

        _pausePolicy = new PausePolicy(_messageWindow,
            () => _manager.Windows.Select(w => w.Monitor).ToList(),
            () => (_settings.PauseWhenMaximized, _settings.PauseOnBattery));
        _pausePolicy.Changed += reasons =>
        {
            _manager.SetPolicyPaused(reasons.ToDictionary(kv => kv.Key, kv => kv.Value != PauseReason.None));
            StateChanged?.Invoke();
        };

        _displayDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _displayDebounce.Tick += async (_, _) =>
        {
            _displayDebounce.Stop();
            await _manager.RebuildAsync("display settings changed");
            _pausePolicy.Evaluate();
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _online = new OnlineInfoService(AppPaths.Cache);
        _online.Updated += () =>
        {
            _manager.Broadcast(_online.RatesMessage());
            _manager.Broadcast(_online.HolidaysMessage());
            _manager.Broadcast(_online.WeatherMessage());
            StateChanged?.Invoke();
        };
        _online.WeatherCity = _settings.WeatherCity;

        _updates = new UpdateService(_requestExit) { AutoCheck = _settings.CheckForUpdates };
        _updates.UpdateAvailable += release =>
            Toast.Show(Loc.F("Chao Desk {0} is available", release.Version), Loc.T("Click to see what is new and install it."), _appIcon,
                () => ShowSettings("general"));

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); _settings.Save(AppPaths.SettingsFile); };

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _previewTimer.Tick += async (_, _) => { _previewTimer.Stop(); await CapturePreviewIfMissingAsync(); };

        // Computer-status widget: sample every 4 s, only while it is shown and a wallpaper is running.
        _systemTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _systemTick.Tick += (_, _) => { if (_anyRunning) _manager.Broadcast(SystemMessage()); };
        UpdateSystemTick();

        // Launcher, clipboard history and file search.
        _hotkeys = new HotkeyManager(_messageWindow);
        _screenshots = new ScreenshotService(ScreenshotOptions) { Recognize = ShowOcr };
        _tray.ScreenshotRequested += () => AfterLauncherCloses(() => _screenshots.StartRegion());
        _screenshots.Pins.PinHotkey = () => _settings.PinHotkey;
        _screenshots.Pins.Restore();
        _stats = LaunchStats.Load(AppPaths.LauncherStats);
        _statsSave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statsSave.Tick += (_, _) => { _statsSave.Stop(); _stats.Save(AppPaths.LauncherStats); };
        _clipHistory = new ClipboardHistory(AppPaths.Clipboard, _settings.ClipboardMaxItems);
        _clipHistory.Load();
        _clipSave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _clipSave.Tick += (_, _) => { _clipSave.Stop(); _clipHistory.Save(); };
        _clipHistory.Changed += () => { _clipSave.Stop(); _clipSave.Start(); };
        _clipMonitor = new ClipboardMonitor(_messageWindow, _clipHistory) { IgnoredApps = new(_settings.ClipboardIgnoredApps, StringComparer.OrdinalIgnoreCase) };
        _clipMonitor.Enabled = _settings.ClipboardEnabled;
        _files.IncludeHidden = _settings.FileSearchIncludeHidden;
        _files.Index.SetSkipJunk(_settings.FileSearchSkipJunk); // before the saved index loads
        _files.Index.SetExcludedFolders(_settings.FileSearchExcluded);
        _files.StatusChanged += () => _messageWindow.Dispatch(() => { if (!_disposed) StateChanged?.Invoke(); });
        ApplyHotkeys();
        ApplyQuickLook();
        _catalog.Refresh();
        if (_settings.FileSearchEnabled) _files.Start();
        _tray.SetWallpaperEnabled(_settings.WallpaperEnabled);

        // Refresh the command in case the app was moved or reinstalled elsewhere.
        if (_settings.StartWithWindows) StartupRegistration.Apply(true);
    }

    public async Task StartAsync()
    {
        _current = ResolvePackage(_settings.CurrentWallpaperId);
        if (WantsDesktopLayer) await StartWallpaperAsync();
        else OnManagerStateChanged();
    }

    /// <summary>The pond, or (with the wallpaper off) the widgets over the Windows wallpaper.</summary>
    private bool WantsDesktopLayer => _settings.WallpaperEnabled || _settings.DesktopWidgets;

    /// <summary>The widgets-only page: lotus-widgets.js from the built-in pond, drawn over the user's Windows wallpaper.</summary>
    private WallpaperContent? WidgetsContent()
    {
        var pkg = _library.Find("lotus-pond");
        if (pkg == null || !File.Exists(Path.Combine(pkg.Folder, "widgets.html"))) return null;
        var desktop = DesktopBackground.Current();
        WallpaperWindow.DesktopImagePath = desktop.ImagePath;
        return new WallpaperContent(pkg.Id, pkg.Folder, "widgets.html", ParseColor(desktop.Color));
    }

    /// <summary>The Windows wallpaper changed (or its fit): the widgets-only page redraws it.</summary>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.Desktop) return;
        _messageWindow.Dispatch(() =>
        {
            if (!_wallpaperRunning || !_widgetsOnly) return;
            var desktop = DesktopBackground.Current();
            WallpaperWindow.DesktopImagePath = desktop.ImagePath;
            _manager.Broadcast(DesktopMessage(desktop).ToJsonString());
        });
    }

    private static JsonObject DesktopMessage(DesktopBackground desktop) => new()
    {
        ["type"] = "desktop",
        ["image"] = desktop.ImagePath == null ? null : $"https://{WallpaperWindow.DesktopHost}/wallpaper?v={Environment.TickCount64}",
        ["fit"] = desktop.Fit,
        ["color"] = desktop.Color,
    };

    /// <summary>Shows the wallpaper (startup, or turned back on in Settings). Needs the WebView2 Runtime.</summary>
    private async Task StartWallpaperAsync()
    {
        if (_wallpaperRunning) return;
        if (_current == null)
        {
            Log.Error("No usable wallpaper found in {Folder}", AppPaths.BuiltInWallpapers);
            MessageBox.Show(Loc.T("No wallpapers were found. Reinstall Chao Desk or import a wallpaper from Settings."),
                "Chao Desk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!App.CheckWebView2Runtime()) return;
        bool widgetsOnly = !_settings.WallpaperEnabled;
        var content = widgetsOnly ? WidgetsContent() : ContentFor(_current);
        if (content == null) { Log.Warning("The widgets page is missing; nothing to show on the desktop"); return; }
        _wallpaperRunning = true;
        _widgetsOnly = widgetsOnly;
        Log.Information(widgetsOnly ? "Showing the widgets over the Windows wallpaper" : "Showing the wallpaper");
        await _manager.StartAsync(content);
        _pausePolicy.Evaluate();
        ApplyMouseInteraction();
        _online.Enabled = _settings.OnlineInfo;
    }

    /// <summary>Removes the wallpaper and what only serves it (mouse hook, online info).</summary>
    private void StopWallpaper()
    {
        if (!_wallpaperRunning) return;
        _wallpaperRunning = false;
        _manager.Stop();
        ApplyMouseInteraction();
        _online.Enabled = false;
        WallpaperWindow.DesktopImagePath = null;
        DesktopAttacher.RefreshDesktop();
        Log.Information(_widgetsOnly ? "Desktop widgets turned off" : "Wallpaper turned off");
        _widgetsOnly = false;
    }

    private WallpaperPackage? ResolvePackage(string id)
    {
        var pkg = _library.Find(id);
        if (pkg != null) return pkg;
        pkg = _library.Find("lotus-pond") ?? _library.Packages.FirstOrDefault();
        if (pkg != null) Log.Warning("Wallpaper {Id} not found; using {Fallback}", id, pkg.Id);
        return pkg;
    }

    private static WallpaperContent ContentFor(WallpaperPackage pkg) =>
        new(pkg.Id, pkg.Folder, pkg.Manifest.Entry, ParseColor(pkg.Manifest.Background));

    private static System.Drawing.Color ParseColor(string hex)
    {
        try { return System.Drawing.ColorTranslator.FromHtml(hex); }
        catch { return System.Drawing.Color.Black; }
    }

    // ---- Settings window ---------------------------------------------------------------------------

    public void ShowSettings(string page = "home")
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.ShowPage(page);
            _settingsWindow.Show();
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
        _settingsWindow.Topmost = true; // bring to front even when launched from a second instance
        _settingsWindow.Topmost = false;
    }

    // ---- ISettingsHost -----------------------------------------------------------------------------

    public AppSettings Settings => _settings;
    public WallpaperLibrary Library => _library;
    public string CurrentWallpaperId => _current?.Id ?? _settings.CurrentWallpaperId;
    public bool DevToolsAvailable => _devTools;
    public UpdateService Updates => _updates;

    public string StatusSummary
    {
        get
        {
            if (!_settings.WallpaperEnabled) return _wallpaperRunning ? Loc.T("Wallpaper off; the widgets show over your Windows wallpaper.") : Loc.T("Wallpaper off.");
            int total = _manager.WindowCount, running = _manager.RunningCount;
            if (total == 0) return Loc.T("Not attached to the desktop.");
            if (_manager.IsUserPaused) return Loc.T("Paused from the tray menu.");
            if (running == total) return total == 1 ? Loc.T("Running on your display.") : Loc.F("Running on all {0} displays.", total);
            var reasons = _pausePolicy.Current.Values.Aggregate(PauseReason.None, (a, b) => a | b);
            string why = DescribePause(reasons);
            if (running == 0) return why.Length > 0 ? Loc.F("Paused — {0}.", why) : Loc.T("Paused.");
            return why.Length > 0
                ? Loc.F("Running on {0} of {1} displays; the others are paused ({2}).", running, total, why)
                : Loc.F("Running on {0} of {1} displays; the others are paused.", running, total);
        }
    }

    public async Task ApplyWallpaperAsync(string id)
    {
        var pkg = _library.Find(id);
        if (pkg == null) return;
        _current = pkg;
        _settings.CurrentWallpaperId = pkg.Id;
        ScheduleSave();
        Log.Information("Applying wallpaper {Id}", pkg.Id);
        if (!_widgetsOnly) await _manager.SetContentAsync(ContentFor(pkg));
        StateChanged?.Invoke();
    }

    public JsonObject CurrentProperties() =>
        _current?.Manifest.MergeProperties(_settings.PropertiesFor(_current.Id)) ?? new JsonObject();

    public void SetProperty(string key, JsonNode value)
    {
        if (_current == null) return;
        var def = _current.Manifest.Properties.FirstOrDefault(p => p.Key == key);
        if (def == null) return;
        _settings.PropertiesFor(_current.Id)[key] = def.Coerce(value);
        BroadcastProperties();
        ScheduleSave();
    }

    public void ResetProperties()
    {
        if (_current == null) return;
        _settings.PropertiesFor(_current.Id).Clear();
        BroadcastProperties();
        ScheduleSave();
    }

    private void BroadcastProperties()
    {
        var msg = new JsonObject { ["type"] = "properties", ["values"] = CurrentProperties() };
        _manager.Broadcast(msg.ToJsonString());
    }

    public void ApplyGeneralSettings()
    {
        _settings.Normalize();
        _manager.Broadcast(JsonSerializer.Serialize(new
        {
            type = "settings",
            fpsLimit = _settings.FpsLimit,
            renderScale = _settings.RenderScale,
            quality = _settings.Quality.ToString().ToLowerInvariant(),
        }));
        _online.WeatherCity = _settings.WeatherCity;
        if (WantsDesktopLayer != _wallpaperRunning || (_wallpaperRunning && _widgetsOnly == _settings.WallpaperEnabled))
        {
            StopWallpaper();
            if (WantsDesktopLayer) _ = StartWallpaperAsync();
            _tray.SetWallpaperEnabled(_settings.WallpaperEnabled);
            OnManagerStateChanged();
        }
        ApplyMouseInteraction();
        _online.Enabled = _settings.OnlineInfo && _wallpaperRunning;
        _updates.AutoCheck = _settings.CheckForUpdates;
        if (_settings.StartWithWindows != StartupRegistration.IsEnabled()) StartupRegistration.Apply(_settings.StartWithWindows);
        _pausePolicy.Evaluate();
        ScheduleSave();
    }

    public IReadOnlyList<(string Section, string Label, string Value)> DiagnosticsRows()
    {
        var rows = new List<(string, string, string)>();
        var t = _manager.Target;
        string desktopSection = Loc.T("Desktop"), renderingSection = Loc.T("Rendering"), onlineSection = Loc.T("Online info"), filesSection = Loc.T("Files");
        rows.Add((desktopSection, Loc.T("Layout"), t == null ? Loc.T("not detected") : $"{t.Layout} ({DescribeLayout(t.Layout)})"));
        rows.Add((desktopSection, Loc.T("Branch"), t?.Branch ?? "–"));
        rows.Add((desktopSection, Loc.T("Parent window"), t == null ? "–" : $"0x{t.Parent:X} ({(t.IsValid ? Loc.T("alive") : Loc.T("lost"))})"));
        rows.Add((desktopSection, Loc.T("Windows version"), $"{Environment.OSVersion.Version} ({Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", "")})"));
        rows.Add((renderingSection, "WebView2 Runtime", _manager.BrowserVersion ?? "–"));
        rows.Add((renderingSection, Loc.T("Wallpaper"), _current == null ? "–" : $"{_current.Manifest.Name} ({_current.Id}) — {_current.Folder}"));
        rows.Add((renderingSection, Loc.T("Mouse hook"), _mouse != null ? Loc.T("installed") : Loc.T("not installed (interaction off)")));
        var reasons = _pausePolicy.Current;
        foreach (var w in _manager.Windows)
        {
            var m = w.Monitor;
            string state = w.Hwnd == IntPtr.Zero ? Loc.T("window lost") : w.IsPaused ? Loc.T("paused") : Loc.T("running");
            string why = reasons.TryGetValue(m.DeviceName, out var r) && r != PauseReason.None ? $" — {r}" : _manager.IsUserPaused ? " — " + Loc.T("paused from tray") : "";
            string stats = _pageStats.TryGetValue(m.DeviceName, out var s) ? $"\n{s}" : "";
            rows.Add((Loc.T("Displays"), m.IsPrimary ? Loc.F("{0} (primary)", m.DeviceName) : m.DeviceName,
                Loc.F("{0}×{1} at ({2}, {3})", m.Bounds.Width, m.Bounds.Height, m.Bounds.X, m.Bounds.Y) + $" · {m.Dpi} DPI ({m.Scale:P0}) · {state}{why}{stats}"));
        }
        foreach (var (folder, errors) in _library.Problems)
            rows.Add((Loc.T("Library problems"), Path.GetFileName(folder), string.Join("\n", errors)));
        rows.Add((onlineSection, Loc.T("Status"), !_settings.OnlineInfo ? Loc.T("off") : _online.LastError ?? Loc.F("{0} exchange rates (updated {1:HH:mm}), {2} holiday-schedule days", _online.Rates.Count, _online.RatesFetchedAt, _online.Holidays.Count)));
        rows.Add((onlineSection, Loc.T("Weather"), _settings.WeatherCity.Length == 0 ? Loc.T("no city set") : _online.WeatherError ?? (_online.Weather is { } wr ? Loc.F("{0}: {1:0.#} °C (updated {2:HH:mm})", wr.Place.Name, wr.Now.Temp, wr.FetchedAt) : Loc.T("waiting"))));
        rows.Add((filesSection, Loc.T("Settings"), AppPaths.SettingsFile));
        rows.Add((filesSection, Loc.T("Logs"), AppPaths.Logs));
        rows.Add((filesSection, Loc.T("User wallpapers"), AppPaths.UserWallpapers));
        return rows;
    }

    private static string DescribePause(PauseReason r)
    {
        var parts = new List<string>();
        if (r.HasFlag(PauseReason.Fullscreen)) parts.Add(Loc.T("a full-screen app is running"));
        if (r.HasFlag(PauseReason.Maximized)) parts.Add(Loc.T("a maximised window covers the desktop"));
        if (r.HasFlag(PauseReason.OnBattery)) parts.Add(Loc.T("running on battery"));
        if (r.HasFlag(PauseReason.BatterySaver)) parts.Add(Loc.T("battery saver is on"));
        if (r.HasFlag(PauseReason.SessionLocked)) parts.Add(Loc.T("the session is locked"));
        if (r.HasFlag(PauseReason.DisplayOff)) parts.Add(Loc.T("the display is off"));
        if (r.HasFlag(PauseReason.Suspended)) parts.Add(Loc.T("the system is sleeping"));
        return string.Join(Loc.Pick(", ", "，"), parts);
    }

    private static string DescribeLayout(DesktopLayout l) => l switch
    {
        DesktopLayout.Raised => "Windows 11 24H2+",
        DesktopLayout.Legacy => Loc.T("Windows 10 / 11 before 24H2"),
        DesktopLayout.Fallback => Loc.T("fallback"),
        _ => Loc.T("unknown"),
    };

    public void OpenLogFolder()
    {
        Directory.CreateDirectory(AppPaths.Logs);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Logs}\"") { UseShellExecute = true });
    }

    public Task ReloadAsync()
    {
        _library.Scan();
        if (_current != null) _current = _library.Find(_current.Id) ?? _current;
        return _current == null ? Task.CompletedTask : _manager.SetContentAsync(ContentFor(_current));
    }

    public void OpenDevTools()
    {
        foreach (var w in _manager.Windows) w.OpenDevTools();
    }

    public ImportResult Import(string path)
    {
        var r = _library.Import(path);
        StateChanged?.Invoke();
        return r;
    }

    public bool RemoveWallpaper(string id)
    {
        bool ok = _library.Remove(id);
        StateChanged?.Invoke();
        return ok;
    }

    // ---- Widgets ---------------------------------------------------------------------------------

    public IReadOnlyList<WidgetItem> Widgets => _settings.Widgets;

    public string WidgetLayout
    {
        get => _settings.WidgetLayout;
        set { if (WidgetCatalog.Layouts.Contains(value) && value != _settings.WidgetLayout) { _settings.WidgetLayout = value; WidgetsChanged(); } }
    }

    public string CardDisplay
    {
        get => _settings.CardDisplay;
        set { if (WidgetCatalog.Displays.Contains(value) && value != _settings.CardDisplay) { _settings.CardDisplay = value; WidgetsChanged(); } }
    }

    public int WidgetsPerPage
    {
        get => _settings.WidgetsPerPage;
        set { if (WidgetCatalog.PerPageOptions.Contains(value) && value != _settings.WidgetsPerPage) { _settings.WidgetsPerPage = value; WidgetsChanged(); } }
    }

    public void SetWidgetEnabled(string id, bool enabled)
    {
        var w = _settings.Widgets.FirstOrDefault(x => x.Id == id);
        if (w == null || w.Enabled == enabled) return;
        w.Enabled = enabled;
        WidgetsChanged();
    }

    public void MoveWidget(string id, int delta) { if (Move(_settings.Widgets, _settings.Widgets.FindIndex(x => x.Id == id), delta)) WidgetsChanged(); }

    public IReadOnlyList<ShortcutItem> Shortcuts => _settings.Shortcuts;

    /// <summary>Adds a shortcut; returns an error message instead when the target is not usable.</summary>
    public string? AddShortcut(string name, string target)
    {
        name = name.Trim(); target = target.Trim().Trim('"');
        if (target.Length > 0 && !target.Contains("://") && target.Contains('.') && !target.Contains('\\') && !target.Contains(':')) target = "https://" + target; // "boc.cn" → a web address
        if (!WidgetCatalog.IsValidTarget(target)) return Loc.T("Enter a web address (https://…) or the full path of an existing file or folder.");
        if (_settings.Shortcuts.Count >= 12) return Loc.T("At most 12 shortcuts.");
        if (name.Length == 0) name = WidgetCatalog.IsWebUrl(target) ? new Uri(target).Host.Replace("www.", "") : Path.GetFileNameWithoutExtension(target.TrimEnd('\\'));
        if (name.Length == 0) name = target;
        _settings.Shortcuts.Add(new ShortcutItem { Name = name.Length > 20 ? name[..20] : name, Target = target });
        WidgetsChanged();
        return null;
    }

    public void RemoveShortcut(string id) { if (_settings.Shortcuts.RemoveAll(x => x.Id == id) > 0) WidgetsChanged(); }

    public void MoveShortcut(string id, int delta) { if (Move(_settings.Shortcuts, _settings.Shortcuts.FindIndex(x => x.Id == id), delta)) WidgetsChanged(); }

    private static bool Move<T>(List<T> list, int index, int delta)
    {
        int to = index + delta;
        if (index < 0 || to < 0 || to >= list.Count) return false;
        (list[index], list[to]) = (list[to], list[index]);
        return true;
    }

    private void WidgetsChanged()
    {
        _manager.Broadcast(WidgetsMessage());
        UpdateSystemTick();
        ScheduleSave();
        StateChanged?.Invoke();
    }

    private void UpdateSystemTick()
    {
        bool on = _settings.Widgets.Any(w => w.Id == "system" && w.Enabled);
        if (on && !_systemTick.IsEnabled) { _systemTick.Start(); _manager.Broadcast(SystemMessage()); }
        else if (!on) _systemTick.Stop();
    }

    private string WidgetsMessage() => JsonSerializer.Serialize(new
    {
        type = "widgets",
        layout = _settings.WidgetLayout,
        display = _settings.CardDisplay,
        perPage = _settings.WidgetsPerPage,
        items = _settings.Widgets.Where(w => w.Enabled).Select(w => w.Id),
        shortcuts = _settings.Shortcuts.Select(x => new { id = x.Id, name = x.Name, kind = x.Kind }),
    });

    private string SystemMessage()
    {
        var s = _system.Sample();
        return JsonSerializer.Serialize(new
        {
            type = "system",
            cpu = s.CpuPercent, mem = s.MemoryPercent, memUsed = s.MemoryUsedGb, memTotal = s.MemoryTotalGb,
            battery = s.BatteryPercent, charging = s.Charging, drive = s.Drive, diskFree = s.DiskFreeGb, diskTotal = s.DiskTotalGb, uptime = s.UptimeHours,
        });
    }

    /// <summary>Pages can only ask for a shortcut by id; the target comes from the user's own list.</summary>
    private void OpenShortcut(string id)
    {
        var x = _settings.Shortcuts.FirstOrDefault(s => s.Id == id);
        if (x == null) return;
        if (!WidgetCatalog.IsValidTarget(x.Target)) { Log.Warning("Shortcut {Name} points to a missing target {Target}", x.Name, x.Target); return; }
        try { Process.Start(new ProcessStartInfo(x.Target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "Could not open shortcut {Name}", x.Name); }
    }

    // ---- Tasks ----------------------------------------------------------------------------------

    public IReadOnlyList<TaskItem> Tasks => _settings.Tasks;

    public void AddTask(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        _settings.Tasks.Add(new TaskItem { Text = text.Length > 120 ? text[..120] : text });
        TasksChanged();
    }

    public void ToggleTask(string id)
    {
        var t = _settings.Tasks.FirstOrDefault(x => x.Id == id);
        if (t == null) return;
        t.Done = !t.Done;
        TasksChanged();
    }

    public void RemoveTask(string id)
    {
        if (_settings.Tasks.RemoveAll(x => x.Id == id) > 0) TasksChanged();
    }

    public void ClearDoneTasks()
    {
        if (_settings.Tasks.RemoveAll(x => x.Done) > 0) TasksChanged();
    }

    private void TasksChanged()
    {
        _manager.Broadcast(TasksMessage());
        ScheduleSave();
        StateChanged?.Invoke();
    }

    private string TasksMessage() => JsonSerializer.Serialize(new
    {
        type = "tasks",
        items = _settings.Tasks.OrderBy(t => t.Done).ThenByDescending(t => t.Created).Select(t => new { id = t.Id, text = t.Text, done = t.Done }),
    });

    /// <summary>Pages may ask to open a link in the browser, but only to the sources we fetch from.</summary>
    private static readonly string[] AllowedLinkHosts = ["www.boc.cn", "boc.cn"];

    private static void OpenAllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !AllowedLinkHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            Log.Warning("Refused to open {Url}", url);
            return;
        }
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    // ---- Page messages / previews ------------------------------------------------------------------

    private void OnPageMessage(WallpaperWindow w, JsonElement msg)
    {
        if (!msg.TryGetProperty("type", out var t)) return;
        switch (t.GetString())
        {
            case "ready":
                // Everything the page shows that comes from the host rather than wallpaper.json.
                w.Post(TasksMessage());
                w.Post(WidgetsMessage());
                if (_systemTick.IsEnabled) w.Post(SystemMessage());
                if (_online.Rates.Count > 0) w.Post(_online.RatesMessage());
                if (_online.Holidays.Count > 0) w.Post(_online.HolidaysMessage());
                w.Post(_online.WeatherMessage());
                // Make sure the page has the latest properties and settings (it got them at injection time,
                // but they may have changed while it loaded).
                if (_widgetsOnly) break; // no pond to take a library preview of
                if (w.Monitor.IsPrimary && _current != null && !_current.HasOwnPreview && !File.Exists(WallpaperLibrary.GeneratedPreviewPath(_current.Id)))
                {
                    _previewTimer.Stop();
                    _previewTimer.Interval = TimeSpan.FromSeconds(4);
                    _previewTimer.Start();
                }
                break;
            case "task-toggle":
                if (msg.TryGetProperty("id", out var tid) && tid.GetString() is { } taskId) ToggleTask(taskId);
                break;
            case "hit-rects":
                SetCaptureRects(w, msg);
                break;
            case "open-shortcut":
                if (msg.TryGetProperty("id", out var sid) && sid.GetString() is { } shortcutId) OpenShortcut(shortcutId);
                break;
            case "open-url":
                if (msg.TryGetProperty("url", out var u) && u.GetString() is { } url) OpenAllowedUrl(url);
                break;
            case "stats":
                string fps = msg.TryGetProperty("fps", out var f) ? f.ToString() : "?";
                string ms = msg.TryGetProperty("frameMs", out var fm) ? fm.ToString() : "?";
                string q = msg.TryGetProperty("quality", out var qq) ? qq.GetString() ?? "" : "";
                _pageStats[w.Monitor.DeviceName] = Loc.F("{0} FPS · {1} ms per frame · quality {2}", fps, ms, q);
                Log.Debug("Page stats {Monitor}: {Fps} FPS, {Ms} ms/frame, quality {Quality}", w.Monitor.DeviceName, fps, ms, q);
                break;
        }
    }

    /// <summary>PLAN §M5: if a package has no preview.png, capture one from the running wallpaper.</summary>
    private async Task CapturePreviewIfMissingAsync()
    {
        if (_current == null || _current.HasOwnPreview) return;
        // Only capture a running wallpaper (a paused one may still be showing its first frame);
        // otherwise try again shortly.
        var w = _manager.Windows.FirstOrDefault(x => x.Monitor.IsPrimary && x.IsPageReady && !x.IsPaused)
                ?? _manager.Windows.FirstOrDefault(x => x.IsPageReady && !x.IsPaused);
        if (w == null)
        {
            _previewTimer.Interval = TimeSpan.FromSeconds(10);
            _previewTimer.Start();
            return;
        }
        string path = WallpaperLibrary.GeneratedPreviewPath(_current.Id);
        Directory.CreateDirectory(AppPaths.Previews);
        string tmp = path + ".tmp";
        if (await w.CapturePreviewAsync(tmp))
        {
            try
            {
                ResizePng(tmp, path, 640);
                Log.Information("Generated preview for {Id}", _current.Id);
                StateChanged?.Invoke();
            }
            catch (Exception ex) { Log.Warning(ex, "Could not save preview"); }
            finally { try { File.Delete(tmp); } catch { /* ignore */ } }
        }
    }

    private static void ResizePng(string src, string dest, int width)
    {
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(src), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        double scale = Math.Min(1.0, (double)width / frame.PixelWidth);
        var scaled = new System.Windows.Media.Imaging.TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(scaled));
        using var fs = File.Create(dest);
        enc.Save(fs);
    }

    // ---- Init JSON --------------------------------------------------------------------------------

    private string BuildInitJson(WallpaperContent content, MonitorInfo monitor, bool paused)
    {
        var pkg = _library.Find(content.Id);
        var init = new JsonObject
        {
            ["paused"] = paused,
            ["language"] = Loc.Language,
            ["properties"] = pkg?.Manifest.MergeProperties(_settings.PropertiesFor(content.Id)) ?? new JsonObject(),
            ["settings"] = new JsonObject
            {
                ["fpsLimit"] = _settings.FpsLimit,
                ["renderScale"] = _settings.RenderScale,
                ["quality"] = _settings.Quality.ToString().ToLowerInvariant(),
            },
            ["monitor"] = new JsonObject { ["name"] = monitor.DeviceName, ["scale"] = monitor.Scale, ["primary"] = monitor.IsPrimary },
        };
        if (content.Entry == "widgets.html") init["desktop"] = DesktopMessage(DesktopBackground.Current());
        return init.ToJsonString();
    }

    // ---- Mouse forwarding ------------------------------------------------------------------------

    private MouseForwarder? _mouse;
    private readonly Dictionary<string, List<PixelRect>> _captureRects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A page reports where its controls are (CSS px); the hook keeps presses there from Explorer.</summary>
    private void SetCaptureRects(WallpaperWindow w, JsonElement msg)
    {
        var list = new List<PixelRect>();
        if (msg.TryGetProperty("rects", out var rects) && rects.ValueKind == JsonValueKind.Array)
            foreach (var r in rects.EnumerateArray().Take(8))
            {
                if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() != 4) continue;
                double s = w.Monitor.Scale, x = r[0].GetDouble(), y = r[1].GetDouble(), wd = r[2].GetDouble(), ht = r[3].GetDouble();
                if (wd <= 0 || ht <= 0 || wd > 4000 || ht > 4000) continue;
                list.Add(new PixelRect(w.Monitor.Bounds.X + (int)Math.Round(x * s), w.Monitor.Bounds.Y + (int)Math.Round(y * s), (int)Math.Round(wd * s), (int)Math.Round(ht * s)));
            }
        _captureRects[w.Monitor.DeviceName] = list;
        _mouse?.SetCaptureRects(_captureRects.Values.SelectMany(x => x).ToList());
    }
    private volatile bool _anyRunning;

    /// <summary>Install or remove the low-level hook to match the setting (removed entirely when off).</summary>
    private void ApplyMouseInteraction()
    {
        bool want = _settings.MouseInteraction && _wallpaperRunning;
        if (want && _mouse == null)
        {
            _mouse = new MouseForwarder(
                e => _messageWindow.Dispatch(() => ForwardPointer(e)),
                () => _anyRunning);
            _mouse.SetCaptureRects(_captureRects.Values.SelectMany(r => r).ToList());
        }
        else if (!want && _mouse != null)
        {
            _mouse.Dispose();
            _mouse = null;
        }
    }

    private void ForwardPointer(DesktopPointerEvent e)
    {
        var w = _manager.WindowAt(e.X, e.Y);
        if (w == null || !w.IsPageReady) return;
        if (e.Kind == PointerEventKind.Move && w.IsPaused) return;
        if (e.Kind != PointerEventKind.Move) Log.Debug("Pointer {Kind} {Button} at ({X},{Y})", e.Kind, e.Button, e.X, e.Y);
        w.Post(PointerJson(e, w.Monitor));
    }

    /// <summary>Physical screen pixels → this page's CSS pixels.</summary>
    internal static string PointerJson(DesktopPointerEvent e, MonitorInfo m)
    {
        double x = (e.X - m.Bounds.X) / m.Scale;
        double y = (e.Y - m.Bounds.Y) / m.Scale;
        string ev = e.Kind switch
        {
            PointerEventKind.Move => "move",
            PointerEventKind.Down => "down",
            PointerEventKind.Up => "up",
            _ => "click",
        };
        string button = e.Button switch
        {
            PointerButton.Left => "left",
            PointerButton.Right => "right",
            PointerButton.Middle => "middle",
            _ => "none",
        };
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{{\"type\":\"pointer\",\"event\":\"{ev}\",\"x\":{x:0.##},\"y\":{y:0.##},\"button\":\"{button}\"}}");
    }

    // ---- Launcher, clipboard, file search ----------------------------------------------------------

    private void ToggleLauncher()
    {
        _launcher ??= new LauncherWindow(new LauncherContext
        {
            Catalog = _catalog,
            Files = _files,
            Stats = _stats,
            StatsChanged = () => { _statsSave.Stop(); _statsSave.Start(); },
            Commands = LauncherCommands,
            FileSearchEnabled = () => _settings.FileSearchEnabled,
            SetUpFileSearch = SetUpFileSearchAsync,
            Preview = ShowQuickLook,
            AppIcon = _appIcon,
            ShowDetails = _settings.LauncherPreview,
            DetailsToggled = on => { _settings.LauncherPreview = on; ScheduleSave(); },
        });
        _launcher.Toggle();
    }

    private void ToggleClipboard()
    {
        if (!_settings.ClipboardEnabled) return;
        if (_clipWindow == null)
        {
            _clipWindow = new ClipboardWindow(_clipHistory, _clipMonitor, _settings.ClipboardPreview) { QuickLook = ShowQuickLook, Recognize = ShowOcr, Translate = TranslateAsync };
            _clipWindow.PreviewToggled += on => { _settings.ClipboardPreview = on; ScheduleSave(); };
        }
        // Pressing a typing hotkey (Shift+`) again closes the popup and types the character it swallowed.
        if (_clipWindow.IsVisible && Hotkey.Parse(_settings.ClipboardHotkey).TypedText() is { } typed)
        {
            _clipWindow.DismissAndType(typed);
            return;
        }
        _clipWindow.Toggle();
    }

    private IReadOnlyList<LauncherCommand> LauncherCommands()
    {
        var list = new List<LauncherCommand>
        {
            new() { Title = Loc.T("Chao Desk settings"), Subtitle = Loc.T("Hotkeys, clipboard, file search and wallpaper"), Keywords = ["preferences", "options"], Run = () => ShowSettings() },
        };
        if (_settings.ClipboardEnabled)
            list.Add(new() { Title = Loc.T("Clipboard history"), Subtitle = _settings.ClipboardHotkey.Length > 0 ? Loc.F("Also {0}", _settings.ClipboardHotkey) : "", Keywords = ["paste", "copied"], Run = () => _messageWindow.Dispatch(ToggleClipboard) });
        if (_wallpaperRunning && !_widgetsOnly)
            list.Add(new()
            {
                Title = _manager.IsUserPaused ? Loc.T("Resume wallpaper") : Loc.T("Pause wallpaper"),
                Run = () => { _manager.SetUserPaused(!_manager.IsUserPaused); OnManagerStateChanged(); },
            });
        list.Add(new() { Title = Loc.T("Screenshot"), Subtitle = HotkeyHint(_settings.ScreenshotHotkey), Keywords = ["capture", "snip", "jietu"], Run = () => AfterLauncherCloses(() => _screenshots.StartRegion()) });
        list.Add(new() { Title = Loc.T("Scrolling screenshot"), Subtitle = HotkeyHint(_settings.ScrollScreenshotHotkey), Keywords = ["long screenshot", "capture page"], Run = () => AfterLauncherCloses(() => _screenshots.StartRegion(CaptureMode.Scroll)) });
        list.Add(new() { Title = Loc.T("Pin an area of the screen"), Subtitle = HotkeyHint(_settings.PinHotkey), Keywords = ["pin", "sticky"], Run = () => AfterLauncherCloses(() => _screenshots.StartPin()) });
        list.Add(new() { Title = Loc.T("Pin the clipboard image"), Keywords = ["paste image", "pin"], Run = () => AfterLauncherCloses(() => _screenshots.PinClipboard()) });
        list.Add(new() { Title = Loc.T("Recognise text on the screen"), Subtitle = HotkeyHint(_settings.OcrHotkey), Keywords = ["ocr", "text from image", "shibie", "wenzi"], Run = () => AfterLauncherCloses(() => _screenshots.StartRegion(CaptureMode.Ocr)) });
        list.Add(new() { Title = Loc.T("Recognise text in the clipboard image"), Keywords = ["ocr", "picture text"], Run = () => AfterLauncherCloses(RecognizeClipboard) });
        list.Add(new() { Title = _screenshots.Pins.Hidden ? Loc.T("Show pinned images") : Loc.T("Hide pinned images"), Subtitle = HotkeyHint(_settings.PinsToggleHotkey), Keywords = ["pins"], Run = () => _screenshots.Pins.ToggleAll() });
        list.Add(new() { Title = Loc.T("Close all pinned images"), Keywords = ["unpin"], Run = () => _screenshots.Pins.CloseAll() });
        list.Add(new() { Title = Loc.T("Lock computer"), Keywords = ["lock screen"], Run = () => NativeMethods.LockWorkStation() });
        list.Add(new() { Title = Loc.T("Exit Chao Desk"), Keywords = ["quit"], Run = () => _messageWindow.Dispatch(_requestExit) });
        return list;
    }

    // ---- Text recognition ------------------------------------------------------------------------------

    /// <summary>Reads the text in a picture: in the text window, or straight to the clipboard with a notice.</summary>
    private void ShowOcr(BitmapSource image)
    {
        if (!image.IsFrozen) image.Freeze();
        if (_settings.OcrCopyDirectly) { _ = CopyRecognizedAsync(image); return; }
        var window = new OcrWindow(image, _settings.OcrLanguage, _settings.OcrMergeLines, _settings.OcrAutoTranslate, _screenshots.PinAtMouse, (language, merge, translate) =>
        {
            _settings.OcrLanguage = language;
            _settings.OcrMergeLines = merge;
            _settings.OcrAutoTranslate = translate;
            ScheduleSave();
        }) { Translate = TranslateAsync };
        window.Show();
        window.Activate();
    }

    private async Task CopyRecognizedAsync(BitmapSource image)
    {
        var result = await TextRecognizer.RecognizeAsync(image, _settings.OcrLanguage);
        string text = OcrLayout.Compose(result, _settings.OcrMergeLines).Text;
        if (text.Length == 0)
        {
            Toast.Show(Loc.T("No text found"), TextRecognizer.IsAvailable ? Loc.T("Try a larger area.") : PaddleOcr.LoadError ?? Loc.T("Text recognition is not available."), image);
            return;
        }
        try { Clipboard.SetDataObject(text, true); }
        catch (System.Runtime.InteropServices.COMException) { }
        string first = text.Split('\n')[0];
        Toast.Show(Loc.F("Copied {0:N0} characters", text.Length), first.Length > 60 ? first[..60] + "…" : first, image, () =>
        {
            _settings.OcrCopyDirectly = false; // just this once: open the window
            ShowOcr(image);
            _settings.OcrCopyDirectly = true;
        });
    }

    private void RecognizeClipboard()
    {
        var image = _screenshots.ClipboardImage(allowText: false);
        if (image == null) { Toast.Show(Loc.T("No picture on the clipboard"), Loc.T("Copy a picture (or take a screenshot) first.")); return; }
        ShowOcr(image);
    }

    public string OcrLanguage
    {
        get => _settings.OcrLanguage;
        set { _settings.OcrLanguage = value; ScheduleSave(); }
    }

    public bool OcrMergeLines
    {
        get => _settings.OcrMergeLines;
        set { _settings.OcrMergeLines = value; ScheduleSave(); }
    }

    public bool OcrAutoTranslate
    {
        get => _settings.OcrAutoTranslate;
        set { _settings.OcrAutoTranslate = value; ScheduleSave(); }
    }

    public bool OcrCopyDirectly
    {
        get => _settings.OcrCopyDirectly;
        set { _settings.OcrCopyDirectly = value; ScheduleSave(); }
    }

    /// <summary>Every global hotkey: its settings name, where it is stored, and what it does.</summary>
    private sealed record HotkeyDef(string Name, Func<string> Get, Action<string> Set, Action Run, Func<bool> Enabled);

    private HotkeyDef[] HotkeyDefs =>
    [
        new("launcher", () => _settings.LauncherHotkey, v => _settings.LauncherHotkey = v, ToggleLauncher, () => true),
        new("clipboard", () => _settings.ClipboardHotkey, v => _settings.ClipboardHotkey = v, ToggleClipboard, () => _settings.ClipboardEnabled),
        new("screenshot", () => _settings.ScreenshotHotkey, v => _settings.ScreenshotHotkey = v, () => _screenshots.StartRegion(), () => true),
        new("quickshot", () => _settings.QuickScreenshotHotkey, v => _settings.QuickScreenshotHotkey = v, () => _screenshots.QuickCapture(), () => true),
        new("scrollshot", () => _settings.ScrollScreenshotHotkey, v => _settings.ScrollScreenshotHotkey = v, () => _screenshots.StartRegion(CaptureMode.Scroll), () => true),
        new("pin", () => _settings.PinHotkey, v => _settings.PinHotkey = v, () => _screenshots.StartPin(), () => true),
        new("pinstoggle", () => _settings.PinsToggleHotkey, v => _settings.PinsToggleHotkey = v, () => _screenshots.Pins.ToggleAll(), () => true),
        new("ocr", () => _settings.OcrHotkey, v => _settings.OcrHotkey = v, () => _screenshots.StartRegion(CaptureMode.Ocr), () => true),
    ];

    /// <summary>Screenshots started from the launcher wait until it has left the screen.</summary>
    private async void AfterLauncherCloses(Action action)
    {
        await Task.Delay(250);
        action();
    }

    private static string HotkeyHint(string hotkey) => hotkey.Length > 0 ? Loc.F("Also {0}", hotkey) : "";

    private void ApplyHotkeys()
    {
        foreach (int id in _hotkeyIds.Values) _hotkeys.Unregister(id);
        _hotkeyIds.Clear();
        _hotkeyProblems.Clear();
        if (!_hotkeysSuspended)
            foreach (var def in HotkeyDefs)
                if (def.Enabled()) RegisterHotkey(def.Name, def.Get(), def.Run);
        _tray.SetHotkeys(_settings.LauncherHotkey, _settings.ClipboardHotkey, _settings.ClipboardEnabled, _settings.ScreenshotHotkey);
    }

    private void RegisterHotkey(string which, string text, Action action)
    {
        if (text.Length == 0) return;
        var key = Hotkey.Parse(text);
        int id = _hotkeys.Register(key, action);
        if (id == 0) _hotkeyProblems[which] = key.ToString(); // the message is made when shown, in the current language
        else _hotkeyIds[which] = id;
    }

    public string HotkeyText(string which) => HotkeyDefs.FirstOrDefault(d => d.Name == which)?.Get() ?? "";
    public string? HotkeyProblem(string which) =>
        _hotkeyProblems.TryGetValue(which, out var key) ? Loc.F("{0} is already taken by another app. Choose another combination.", key) : null;

    /// <summary>Sets a hotkey from the settings recorder; returns an error message or null.</summary>
    public string? SetHotkey(string which, string text)
    {
        var def = HotkeyDefs.FirstOrDefault(d => d.Name == which);
        if (def == null) return null;
        var key = Hotkey.Parse(text);
        if (text.Length > 0 && !key.IsUsable) return Loc.T("Use a combination with Ctrl, Alt, Shift or Win (or an F key).");
        string value = text.Length == 0 ? "" : key.ToString();
        if (value.Length > 0 && HotkeyDefs.Any(d => d.Name != which && d.Get() == value)) return Loc.T("That combination is already used by another Chao Desk hotkey.");
        def.Set(value);
        ApplyHotkeys();
        ScheduleSave();
        StateChanged?.Invoke();
        return HotkeyProblem(which);
    }

    /// <summary>The Home page's buttons.</summary>
    public void OpenTool(string tool)
    {
        switch (tool)
        {
            case "launcher": _messageWindow.Dispatch(ToggleLauncher); break;
            case "clipboard": _messageWindow.Dispatch(ToggleClipboard); break;
            case "screenshot": AfterLauncherCloses(() => _screenshots.StartRegion()); break;
            case "ocr": AfterLauncherCloses(() => _screenshots.StartRegion(CaptureMode.Ocr)); break;
        }
    }

    // ---- Appearance ----

    public string Language
    {
        get => _settings.Language;
        set
        {
            if (value == _settings.Language || !Loc.Modes.Contains(value)) return;
            _settings.Language = value;
            ScheduleSave();
            string before = Loc.Language;
            Loc.Apply(value); // open windows switch their XAML text
            if (Loc.Language == before) return;
            // Windows that build text in code are rebuilt on their next use; the settings window reopens on its page.
            _launcher?.Close();
            _launcher = null;
            _clipWindow?.Close();
            _clipWindow = null;
            _tray.Localize();
            _manager.Broadcast(LanguageMessage());
            OnManagerStateChanged(); // the tray tooltip
            if (_settingsWindow != null)
            {
                _settingsWindow.Close();
                _messageWindow.Dispatch(() => ShowSettings("general"));
            }
        }
    }

    /// <summary>Tells the pages the app's language (the widgets follow it unless the card has its own).</summary>
    private static string LanguageMessage() => JsonSerializer.Serialize(new { type = "language", language = Loc.Language });

    public string TranslateEngine
    {
        get => _settings.TranslateEngine;
        set { if (Translator.Engines.Contains(value)) { _settings.TranslateEngine = value; ScheduleSave(); } }
    }

    public string TranslateTarget
    {
        get => _settings.TranslateTarget;
        set { if (Translator.Targets.Contains(value)) { _settings.TranslateTarget = value; ScheduleSave(); } }
    }

    /// <summary>Translates with the user's engine and target; throws when no engine could be reached.</summary>
    public Task<Translation> TranslateAsync(string text, CancellationToken token = default) =>
        Translator.TranslateAsync(text, _settings.TranslateTarget, _settings.TranslateEngine, token);

    public string Theme
    {
        get => _settings.Theme;
        set
        {
            if (value == _settings.Theme || !ThemeManager.Modes.Contains(value)) return;
            _settings.Theme = value;
            ScheduleSave();
            ThemeManager.Apply(value);
            // Parts of the settings window are built in code with the old colours: reopen it on the same page.
            if (_settingsWindow != null)
            {
                _settingsWindow.Close();
                _messageWindow.Dispatch(() => ShowSettings("general"));
            }
        }
    }

    // ---- Quick Look ----

    private void ApplyQuickLook()
    {
        if (_settings.QuickLookEnabled && _quickLookHook == null)
            _quickLookHook = new QuickLookHook(window => _messageWindow.Dispatch(() => OnQuickLookKey(window)));
        else if (!_settings.QuickLookEnabled && _quickLookHook != null)
        {
            _quickLookHook.Dispose();
            _quickLookHook = null;
        }
    }

    /// <summary>Space in Explorer: preview the selection (Space again on the same file, after clicking back, closes it).</summary>
    private void OnQuickLookKey(IntPtr explorerWindow)
    {
        string? path = ExplorerSelection.SelectedPath(explorerWindow);
        if (path == null) return;
        if (_quickLook is { IsVisible: true } open && string.Equals(open.CurrentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            open.Close();
            return;
        }
        ShowQuickLook(path);
    }

    public void ShowQuickLook(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if (_quickLook == null)
        {
            _quickLook = new QuickLookWindow();
            _quickLook.Closed += (_, _) => _quickLook = null;
        }
        _quickLook.ShowPath(path);
    }

    public bool QuickLookEnabled
    {
        get => _settings.QuickLookEnabled;
        set { _settings.QuickLookEnabled = value; ApplyQuickLook(); ScheduleSave(); }
    }

    // ---- Screenshot settings ----

    private ScreenshotOptions ScreenshotOptions() => new(ScreenshotFolder, _settings.ScreenshotJpegQuality > 0, _settings.ScreenshotJpegQuality,
        _settings.ScreenshotAutoSave, _settings.QuickScreenshotSaves);

    public string ScreenshotFolder => _settings.ScreenshotFolder.Length > 0 ? _settings.ScreenshotFolder
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Chao Desk");

    public void SetScreenshotFolder(string folder) { _settings.ScreenshotFolder = folder.Trim(); ScheduleSave(); }

    /// <summary>0 = lossless PNG; otherwise the JPEG quality (60–95).</summary>
    public int ScreenshotJpegQuality
    {
        get => _settings.ScreenshotJpegQuality;
        set { _settings.ScreenshotJpegQuality = value; _settings.Normalize(); ScheduleSave(); }
    }

    public bool ScreenshotAutoSave
    {
        get => _settings.ScreenshotAutoSave;
        set { _settings.ScreenshotAutoSave = value; ScheduleSave(); }
    }

    public bool QuickScreenshotSaves
    {
        get => _settings.QuickScreenshotSaves;
        set { _settings.QuickScreenshotSaves = value; ScheduleSave(); }
    }

    /// <summary>While the settings recorder has focus, combinations must reach it instead of opening popups.</summary>
    public void SuspendHotkeys(bool suspend)
    {
        if (_hotkeysSuspended == suspend) return;
        _hotkeysSuspended = suspend;
        ApplyHotkeys();
    }

    public bool ClipboardEnabled
    {
        get => _settings.ClipboardEnabled;
        set
        {
            _settings.ClipboardEnabled = value;
            _clipMonitor.Enabled = value;
            if (!value) _clipWindow?.Hide();
            ApplyHotkeys();
            ScheduleSave();
        }
    }

    public int ClipboardMaxItems
    {
        get => _settings.ClipboardMaxItems;
        set
        {
            if (!ClipboardHistory.AllowedSizes.Contains(value)) return;
            _settings.ClipboardMaxItems = value;
            _clipHistory.MaxItems = value;
            _clipHistory.Trim();
            _clipSave.Stop();
            _clipSave.Start();
            ScheduleSave();
        }
    }

    public string ClipboardIgnoredApps
    {
        get => string.Join(", ", _settings.ClipboardIgnoredApps);
        set
        {
            _settings.ClipboardIgnoredApps = value.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase)).ToList();
            _settings.Normalize();
            _clipMonitor.IgnoredApps = new(_settings.ClipboardIgnoredApps, StringComparer.OrdinalIgnoreCase);
            ScheduleSave();
        }
    }

    public int ClipboardCount => _clipHistory.Items.Count;

    public void ClearClipboardHistory()
    {
        _clipHistory.ClearUnpinned();
        StateChanged?.Invoke();
    }

    public bool FileSearchEnabled
    {
        get => _settings.FileSearchEnabled;
        set
        {
            if (_settings.FileSearchEnabled == value) return;
            _settings.FileSearchEnabled = value;
            if (value) _files.Start(); else _files.Stop();
            ScheduleSave();
            StateChanged?.Invoke();
        }
    }

    public bool FileSearchIncludeHidden
    {
        get => _settings.FileSearchIncludeHidden;
        set { _settings.FileSearchIncludeHidden = value; _files.IncludeHidden = value; ScheduleSave(); }
    }

    public bool FileSearchSkipJunk
    {
        get => _settings.FileSearchSkipJunk;
        set
        {
            _settings.FileSearchSkipJunk = value;
            ScheduleSave();
            Task.Run(() =>
            {
                if (_files.Index.SetSkipJunk(value) && _settings.FileSearchEnabled) _files.Rebuild(); // the skipped files are needed back
                _messageWindow.Dispatch(() => { if (!_disposed) StateChanged?.Invoke(); });
            });
        }
    }

    public IReadOnlyList<string> FileSearchExcluded => _settings.FileSearchExcluded;

    public void SetFileSearchExcluded(IEnumerable<string> folders)
    {
        _settings.FileSearchExcluded = folders.ToList();
        _settings.Normalize();
        var excluded = _settings.FileSearchExcluded.ToList();
        Task.Run(() => _files.Index.SetExcludedFolders(excluded));
        ScheduleSave();
        StateChanged?.Invoke();
    }

    public FileIndexState FileSearchState => _files.State;
    public string FileSearchStatus => _settings.FileSearchEnabled ? _files.StatusText : Loc.T("File search is off.");
    public long FileIndexMemory => _files.Index.MemoryBytes;

    public async Task SetUpFileSearchAsync()
    {
        _launcher?.Hide();
        _settings.FileSearchEnabled = true;
        ScheduleSave();
        await _files.SetUpAsync();
        StateChanged?.Invoke();
    }

    public async Task RemoveFileSearchAsync()
    {
        await _files.RemoveAsync();
        _settings.FileSearchEnabled = false;
        ScheduleSave();
        StateChanged?.Invoke();
    }

    private static ImageSource? LoadAppIcon()
    {
        using var s = typeof(App).Assembly.GetManifestResourceStream("ChaoDesk.Host.Assets.chaodesk.ico");
        if (s == null) return null;
        var decoder = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        frame.Freeze();
        return frame;
    }

    // ---- Misc --------------------------------------------------------------------------------------

    private void ScheduleSave()
    {
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _messageWindow.Dispatch(() =>
        {
            Log.Information("Display settings changed");
            _displayDebounce.Stop();
            _displayDebounce.Start();
        });
    }

    private void OnManagerStateChanged()
    {
        if (_disposed) return;
        _anyRunning = _manager.RunningCount > 0;
        _tray.SetPaused(_manager.IsUserPaused);
        int total = _manager.WindowCount, running = _manager.RunningCount;
        string state = !_settings.WallpaperEnabled ? ""
            : total == 0 ? Loc.T("wallpaper not attached")
            : _manager.IsUserPaused ? Loc.T("wallpaper paused")
            : total == 1 ? Loc.F("wallpaper on {0} of {1} display", running, total)
            : Loc.F("wallpaper on {0} of {1} displays", running, total);
        _tray.SetToolTip(state.Length == 0 ? "Chao Desk" : $"Chao Desk — {state}");
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Preview.PreviewHandlerHost.ShutDownServers(); // Word's preview server would otherwise stay behind
        _displayDebounce.Stop();
        _previewTimer.Stop();
        _systemTick.Stop();
        _online.Dispose();
        _updates.Dispose();
        _settingsWindow?.Close();
        _launcher?.Close();
        _clipWindow?.Close();
        _hotkeys.Dispose();
        _quickLookHook?.Dispose();
        _screenshots.Pins.SaveNow();
        _quickLook?.Close();
        _clipMonitor.Dispose();
        _clipSave.Stop();
        _clipHistory.Save();
        _statsSave.Stop();
        _stats.Save(AppPaths.LauncherStats);
        _files.Dispose();
        _catalog.Dispose();
        _mouse?.Dispose();
        _mouse = null;
        _pausePolicy.Dispose();
        _explorer.Dispose();
        _manager.Dispose();
        _tray.Dispose();
        _messageWindow.Dispose();
        _saveDebounce.Stop();
        _settings.Save(AppPaths.SettingsFile);
    }
}
