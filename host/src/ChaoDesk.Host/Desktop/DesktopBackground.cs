using System.IO;
using System.Text;
using Microsoft.Win32;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Desktop;

/// <summary>
/// The user's own Windows wallpaper: its picture, how it is placed (Settings → Personalisation → Background → "Choose
/// a fit") and the background colour. The widgets-only page draws it itself, because our window covers the desktop.
/// </summary>
public sealed record DesktopBackground(string? ImagePath, string Fit, string Color)
{
    /// <summary>CSS-ish fit names: cover (Fill, Span), contain (Fit), fill (Stretch), center, tile.</summary>
    public static DesktopBackground Current()
    {
        string? image = null;
        var sb = new StringBuilder(1024);
        if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0) && File.Exists(sb.ToString())) image = sb.ToString();
        // Spotlight, slideshows and some theme pictures report no file (or a deleted one); Windows keeps the
        // picture it is actually showing here.
        string transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
        if (image == null && sb.Length > 0 && File.Exists(transcoded)) image = transcoded;

        string fit = "cover";
        using (var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
        {
            string style = desktop?.GetValue("WallpaperStyle") as string ?? "10";
            bool tile = desktop?.GetValue("TileWallpaper") as string == "1";
            fit = style switch
            {
                "6" => "contain",
                "2" => "fill",
                "0" => tile ? "tile" : "center",
                _ => "cover", // 10 Fill, 22 Span (one picture per monitor here)
            };
        }

        string color = "#000000";
        using (var colors = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors"))
        {
            var parts = (colors?.GetValue("Background") as string ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts.All(p => byte.TryParse(p, out _)))
                color = "#" + string.Concat(parts.Select(p => byte.Parse(p).ToString("X2")));
        }
        return new DesktopBackground(image, fit, color);
    }

    /// <summary>The picture's content type, from its first bytes (TranscodedWallpaper has no extension).</summary>
    public static string ContentType(string path)
    {
        Span<byte> head = stackalloc byte[4];
        try
        {
            using var fs = File.OpenRead(path);
            int n = fs.Read(head);
            head = head[..n];
        }
        catch (IOException) { return "image/jpeg"; }
        if (head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 })) return "image/png";
        if (head.StartsWith("BM"u8)) return "image/bmp";
        if (head.StartsWith("GIF8"u8)) return "image/gif";
        return "image/jpeg";
    }
}
