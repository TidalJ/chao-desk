using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Settings;

/// <summary>
/// Light, dark or follow Windows. Swaps the colour dictionary (Themes/Light.xaml ↔ Themes/Dark.xaml, same keys)
/// that every window references with DynamicResource, and gives title bars the matching Windows frame. To add a
/// theme: copy Themes/Light.xaml, change the colours, and add it here and to the settings list.
/// </summary>
public static class ThemeManager
{
    public static readonly string[] Modes = ["system", "light", "dark"];

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private static string _mode = "system";
    private static bool _hooked;
    private static bool _initialised;

    public static bool IsDark { get; private set; }

    /// <summary>Raised after the colours changed (windows that built parts in code may want to rebuild them).</summary>
    public static event Action? Changed;

    public static void Apply(string mode)
    {
        _mode = Modes.Contains(mode) ? mode : "system";
        if (!_hooked)
        {
            _hooked = true;
            // Title bars of windows created later follow the theme too.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => { if (s is Window w) ApplyFrame(w); }));
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _mode == "system")
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply("system"));
            };
        }
        bool dark = _mode == "dark" || (_mode == "system" && WindowsUsesDarkApps());
        if (dark == IsDark && Application.Current.Resources.MergedDictionaries.Count > 0 && _initialised) return;
        _initialised = true;
        IsDark = dark;
        var merged = Application.Current.Resources.MergedDictionaries;
        var colours = new ResourceDictionary { Source = new Uri(dark ? "/Themes/Dark.xaml" : "/Themes/Light.xaml", UriKind.Relative) };
        if (merged.Count > 0) merged[0] = colours; else merged.Add(colours);
        foreach (Window w in Application.Current.Windows) ApplyFrame(w);
        Changed?.Invoke();
    }

    private static bool WindowsUsesDarkApps()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private static void ApplyFrame(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }
}
