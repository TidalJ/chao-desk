using System.IO;

namespace ChaoDesk.Host;

/// <summary>Well-known folders. Everything user-specific lives under %LOCALAPPDATA%\ChaoDesk.</summary>
public static class AppPaths
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DataRoot { get; } = Path.Combine(LocalAppData, "ChaoDesk");

    public static string Logs => Path.Combine(DataRoot, "logs");
    public static string WebView2UserData => Path.Combine(DataRoot, "WebView2");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string UserWallpapers => Path.Combine(DataRoot, "wallpapers");
    public static string Previews => Path.Combine(DataRoot, "previews");
    public static string Cache => Path.Combine(DataRoot, "cache");
    public static string Clipboard => Path.Combine(DataRoot, "clipboard");
    public static string LauncherStats => Path.Combine(DataRoot, "launcher.json");

    /// <summary>Wallpapers shipped next to the executable.</summary>
    public static string BuiltInWallpapers => Path.Combine(AppContext.BaseDirectory, "wallpapers");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(UserWallpapers);
        Directory.CreateDirectory(Previews);
        Directory.CreateDirectory(Clipboard);
    }
}
