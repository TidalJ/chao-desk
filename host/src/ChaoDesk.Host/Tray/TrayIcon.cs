using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using ChaoDesk.Host.Localization;

namespace ChaoDesk.Host.Tray;

/// <summary>Notification-area icon and its context menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _pauseItem;
    private readonly MenuItem? _settingsItem;
    private readonly MenuItem _launcherItem;
    private readonly MenuItem _clipboardItem;
    private readonly MenuItem _screenshotItem;
    private readonly Separator _pauseSeparator;

    public event Action? LauncherRequested;
    public event Action? ClipboardRequested;
    public event Action? ScreenshotRequested;
    public event Action? SettingsRequested;
    public event Action? AddTaskRequested;
    public event Action? PauseToggled;
    public event Action? DevToolsRequested;
    public event Action? ExitRequested;

    public TrayIcon(bool showSettings, bool showDevTools)
    {
        var menu = new ContextMenu();

        _launcherItem = new MenuItem { Header = Loc.T("Search apps and files") };
        _launcherItem.Click += (_, _) => LauncherRequested?.Invoke();
        menu.Items.Add(_launcherItem);
        _clipboardItem = new MenuItem { Header = Loc.T("Clipboard history") };
        _clipboardItem.Click += (_, _) => ClipboardRequested?.Invoke();
        menu.Items.Add(_clipboardItem);
        _screenshotItem = new MenuItem { Header = Loc.T("Screenshot") };
        _screenshotItem.Click += (_, _) => ScreenshotRequested?.Invoke();
        menu.Items.Add(_screenshotItem);
        menu.Items.Add(new Separator());

        if (showSettings)
        {
            _settingsItem = new MenuItem { Header = Loc.T("Settings…"), FontWeight = System.Windows.FontWeights.SemiBold };
            _settingsItem.Click += (_, _) => SettingsRequested?.Invoke();
            menu.Items.Add(_settingsItem);
            var addTask = new MenuItem { Header = Loc.T("Add task…") };
            addTask.Click += (_, _) => AddTaskRequested?.Invoke();
            menu.Items.Add(addTask);
            menu.Items.Add(new Separator());
        }

        _pauseItem = new MenuItem { Header = Loc.T("Pause wallpaper") };
        _pauseItem.Click += (_, _) => PauseToggled?.Invoke();
        menu.Items.Add(_pauseItem);

        if (showDevTools)
        {
            var dev = new MenuItem { Header = Loc.T("Open DevTools") };
            dev.Click += (_, _) => DevToolsRequested?.Invoke();
            menu.Items.Add(dev);
        }

        _pauseSeparator = new Separator();
        menu.Items.Add(_pauseSeparator);
        var exit = new MenuItem { Header = Loc.T("Exit") };
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exit);

        _icon = new TaskbarIcon
        {
            ToolTipText = "Chao Desk",
            ContextMenu = menu,
            MenuActivation = PopupActivationMode.RightClick,
            Icon = LoadIcon(),
        };
        _icon.TrayMouseDoubleClick += (_, _) => SettingsRequested?.Invoke();
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void SetPaused(bool userPaused) => _pauseItem.Header = userPaused ? Loc.T("Resume wallpaper") : Loc.T("Pause wallpaper");

    /// <summary>Hide the wallpaper's pause item while the wallpaper is turned off.</summary>
    public void SetWallpaperEnabled(bool enabled)
    {
        var v = enabled ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        _pauseItem.Visibility = v;
        _pauseSeparator.Visibility = v;
    }

    /// <summary>Show the hotkeys next to the menu items ("Alt+Space").</summary>
    public void SetHotkeys(string launcher, string clipboard, bool clipboardEnabled, string screenshot)
    {
        _screenshotItem.InputGestureText = screenshot;
        _launcherItem.InputGestureText = launcher;
        _clipboardItem.InputGestureText = clipboard;
        _clipboardItem.Visibility = clipboardEnabled ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public void SetToolTip(string text)
    {
        // Notification-area tooltips are limited to 127 characters.
        _icon.ToolTipText = text.Length > 127 ? text[..127] : text;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        using var s = typeof(TrayIcon).Assembly.GetManifestResourceStream("ChaoDesk.Host.Assets.chaodesk.ico")
                      ?? throw new InvalidOperationException("Embedded icon missing");
        // The notification area uses the small icon size (16 px at 100 %, 20 at 125 %, 24 at 150 %); the .ico has each frame.
        int size = Math.Max(16, Desktop.NativeMethods.GetSystemMetrics(Desktop.NativeMethods.SM_CXSMICON));
        return new System.Drawing.Icon(s, size, size);
    }

    /// <summary>Puts the menu into the current interface language.</summary>
    public void Localize() { if (_icon.ContextMenu != null) Localization.Loc.Localize(_icon.ContextMenu); }

    public void Dispose() => _icon.Dispose();
}
