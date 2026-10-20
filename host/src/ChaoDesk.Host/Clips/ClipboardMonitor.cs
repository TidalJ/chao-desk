using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChaoDesk.Host.Desktop;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Clips;

/// <summary>
/// Watches the clipboard (WM_CLIPBOARDUPDATE on the message window) and records text, images and copied files
/// into the <see cref="ClipboardHistory"/>. Respects the formats password managers use to opt out
/// ("ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore", CanIncludeInClipboardHistory = 0).
/// UI thread only.
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const long MaxImagePixels = 40_000_000;

    private readonly MessageWindow _window;
    private readonly ClipboardHistory _history;
    private readonly DispatcherTimer _settle;
    private readonly Dictionary<string, string> _appNames = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _foregroundAtCopy;
    private long _ignoreUntil;
    private bool _listening;

    /// <summary>Process names (without .exe) whose copies are never recorded.</summary>
    public HashSet<string> IgnoredApps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ClipboardMonitor(MessageWindow window, ClipboardHistory history)
    {
        _window = window;
        _history = history;
        // Give the source app a moment to finish putting all its formats on the clipboard.
        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _settle.Tick += (_, _) => { _settle.Stop(); Capture(); };
        _window.Message += OnMessage;
    }

    public bool Enabled
    {
        get => _listening;
        set
        {
            if (value == _listening) return;
            if (value)
            {
                _listening = AddClipboardFormatListener(_window.Handle);
                if (!_listening) Log.Warning("AddClipboardFormatListener failed ({Error})", Marshal.GetLastWin32Error());
            }
            else
            {
                RemoveClipboardFormatListener(_window.Handle);
                _listening = false;
            }
        }
    }

    /// <summary>Our own clipboard writes (paste from history) are not recorded again.</summary>
    public void IgnoreNextChange() => _ignoreUntil = Environment.TickCount64 + 600;

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_CLIPBOARDUPDATE) return;
        _foregroundAtCopy = GetForegroundWindow();
        _settle.Stop();
        _settle.Start();
    }

    private void Capture()
    {
        if (Environment.TickCount64 < _ignoreUntil) return;
        string? exe = GetWindowProcessPath(_foregroundAtCopy);
        string process = exe == null ? "" : Path.GetFileNameWithoutExtension(exe);
        if (process.Length > 0 && IgnoredApps.Contains(process)) return;

        IDataObject? data = null;
        for (int attempt = 0; attempt < 5 && data == null; attempt++)
        {
            try { data = System.Windows.Clipboard.GetDataObject(); }
            catch (COMException) { Thread.Sleep(30); } // another app has the clipboard open
        }
        if (data == null) return;
        try
        {
            if (IsExcluded(data)) return;
            var item = Read(data, out byte[]? png);
            if (item == null) return;
            item.Source = AppName(exe);
            _history.Add(item, png);
        }
        catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException or IOException or NotSupportedException)
        {
            Log.Debug(ex, "Could not read the clipboard");
        }
    }

    private static bool IsExcluded(IDataObject data)
    {
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore")) return true;
        if (data.GetDataPresent("CanIncludeInClipboardHistory") && data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4)
            return BitConverter.ToInt32(ms.ToArray(), 0) == 0;
        return false;
    }

    private static ClipItem? Read(IDataObject data, out byte[]? png)
    {
        png = null;
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            string text = string.Join("\n", files);
            return new ClipItem { Kind = ClipKind.Files, Text = text, Hash = ClipItem.HashOf(ClipKind.Files, text) };
        }
        if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string s && !string.IsNullOrWhiteSpace(s))
            return new ClipItem { Kind = ClipKind.Text, Text = s, Hash = ClipItem.HashOf(ClipKind.Text, s) };

        var image = ReadImage(data);
        if (image == null || (long)image.PixelWidth * image.PixelHeight > MaxImagePixels) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        png = stream.ToArray();
        return new ClipItem
        {
            Kind = ClipKind.Image,
            ImageWidth = image.PixelWidth,
            ImageHeight = image.PixelHeight,
            Hash = ClipItem.HashOf(ClipKind.Image, PixelBytes(image)),
        };
    }

    /// <summary>Prefers the "PNG" format (keeps transparency; browsers and Office provide it) over the DIB.</summary>
    private static BitmapSource? ReadImage(IDataObject data)
    {
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream pngStream)
        {
            try
            {
                var frame = BitmapFrame.Create(pngStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                frame.Freeze();
                return frame;
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException) { /* fall back to the DIB */ }
        }
        return data.GetDataPresent(DataFormats.Bitmap) ? System.Windows.Clipboard.GetImage() : null;
    }

    private static byte[] PixelBytes(BitmapSource image)
    {
        var converted = image.Format == System.Windows.Media.PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>"Microsoft Edge" rather than "msedge" when the executable says so.</summary>
    private string AppName(string? exe)
    {
        if (exe == null) return "";
        if (_appNames.TryGetValue(exe, out var name)) return name;
        try { name = FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim() ?? ""; }
        catch { name = ""; }
        if (name.Length == 0 || name.Length > 40) name = Path.GetFileNameWithoutExtension(exe);
        _appNames[exe] = name;
        return name;
    }

    public void Dispose()
    {
        Enabled = false;
        _settle.Stop();
        _window.Message -= OnMessage;
    }
}
