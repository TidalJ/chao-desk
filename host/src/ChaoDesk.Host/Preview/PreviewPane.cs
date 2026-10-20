using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChaoDesk.Host.Launcher;
using ChaoDesk.Host.Localization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Preview;

/// <summary>
/// Shows one file or folder without opening it; used by the Quick Look window and the launcher's preview pane.
/// Images through WIC; GIF/SVG/WebP/AVIF, PDF, web pages and browser-playable media through WebView2; other video
/// through Media Foundation; Office documents through their installed preview handler, or their text when that
/// fails; text and code; zip archives and folders as listings; fonts as a specimen; anything else as the shell's
/// thumbnail with details.
/// </summary>
public sealed class PreviewPane : ContentControl
{
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".ico", ".jxr", ".wdp", ".heic", ".heif" };
    private static readonly HashSet<string> WebImageExts = new(StringComparer.OrdinalIgnoreCase) { ".gif", ".svg", ".webp", ".avif" };
    private static readonly HashSet<string> WebMediaExts = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".webm", ".mp3", ".m4a", ".aac", ".wav", ".ogg", ".oga", ".opus", ".flac" };
    private static readonly HashSet<string> SystemVideoExts = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".avi", ".wmv", ".mov", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp", ".wma" };
    private static readonly HashSet<string> WebDocExts = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".htm", ".html", ".mht", ".mhtml" };
    private static readonly HashSet<string> ZipExts = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".jar", ".apk", ".nupkg", ".vsix", ".epub", ".xpi", ".whl" };
    private static readonly HashSet<string> FontExts = new(StringComparer.OrdinalIgnoreCase) { ".ttf", ".otf", ".ttc" };
    private static Task<CoreWebView2Environment>? _webEnvironment;

    private Image? _image;              // the picture being shown, if any
    private ScrollViewer? _imageScroll;
    private WebView2? _web;
    private MediaElement? _media;
    private string _baseDetails = "";

    public string CurrentPath { get; private set; } = "";
    public string Details { get; private set; } = "";

    /// <summary>The picture's size in pixels, when the preview is an image (the window sizes itself to it).</summary>
    public Size? ImageSize { get; private set; }

    /// <summary>An Office document whose text can be shown instead of (or after) the rendered preview.</summary>
    public bool CanShowText => OfficeText.Extensions.Contains(Path.GetExtension(CurrentPath));

    public event Action? DetailsChanged;

    /// <summary>In the launcher's details column: show video and audio as a still with their details instead of playing them.</summary>
    public bool Inline { get; init; }

    /// <summary>A web page asked to close the preview (Space/Esc pressed inside it).</summary>
    public event Action? CloseRequested;

    public PreviewPane()
    {
        Focusable = false;
        Unloaded += (_, _) => Release();
    }

    public void Load(string path)
    {
        Release();
        CurrentPath = path;
        ImageSize = null;
        bool isDir = Directory.Exists(path);
        _baseDetails = Describe(path, isDir);
        SetDetails("");
        try
        {
            string ext = Path.GetExtension(path);
            if (isDir) Content = FolderView(path);
            else if (ImageExts.Contains(ext) && ImageView(path) is { } image) Content = image;
            else if (WebImageExts.Contains(ext)) Content = WebView(path, centredImage: true);
            else if (Inline && (WebMediaExts.Contains(ext) || SystemVideoExts.Contains(ext))) Content = InfoView(path, Loc.T("Shift+Enter plays it in Quick Look · double-click opens it"));
            else if (WebMediaExts.Contains(ext) || WebDocExts.Contains(ext)) Content = WebView(path, centredImage: false);
            else if (SystemVideoExts.Contains(ext)) Content = VideoView(path);
            else if (ZipExts.Contains(ext)) Content = ZipView(path);
            else if (FontExts.Contains(ext)) Content = FontView(path) ?? InfoView(path);
            else if (OfficeText.Extensions.Contains(ext) && FromInternet(path)) ShowBlockedOffice(path);
            else if (PreviewHandlerHost.HandlerFor(path) is { } clsid && !TextFiles.HasTextExtension(path)) Content = HandlerView(path, clsid);
            else if (OfficeText.Extensions.Contains(ext)) ShowText();
            else if (new FileInfo(path).Length <= 64L * 1024 * 1024 && TextView(path) is { } text) Content = text;
            else Content = InfoView(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Information(ex, "Could not preview {Path}", CurrentPath);
            Content = InfoView(path, ex.Message);
        }
    }

    /// <summary>For Office documents: the text, read from the file itself (always works, no formatting).</summary>
    public void ShowText(string? note = null)
    {
        Release();
        string? text = OfficeText.Extract(CurrentPath);
        if (text == null) { Content = InfoView(CurrentPath, note ?? Loc.T("This document could not be read.")); return; }
        Content = TextBox(text, wrap: true);
        SetDetails(Loc.F("{0} · {1:N0} lines", note ?? Loc.T("text only"), text.Count(c => c == '\n') + 1));
    }

    /// <summary>
    /// A downloaded file (the "from the internet" mark Windows adds): Office's previewers refuse it (Protected View),
    /// as they do in Explorer. Show its text, and let the user unblock it (as Properties → Unblock does) to see it rendered.
    /// </summary>
    private void ShowBlockedOffice(string path)
    {
        ShowText(Loc.T("downloaded from the internet — text only"));
        if (Content is not TextBox text) return;
        var link = new TextBlock { Text = Loc.T("Unblock and preview"), TextDecorations = TextDecorations.Underline, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(16, 0, 0, 0) };
        link.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        link.MouseLeftButtonUp += (_, _) =>
        {
            try { File.Delete(path + ":Zone.Identifier"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Information(ex, "Could not unblock {Path}", path); return; }
            Log.Information("Unblocked {Path}", path);
            Load(path);
        };
        var note = new TextBlock { Text = Loc.T("Downloaded from the internet: Office only previews it once you unblock it."), TextWrapping = TextWrapping.Wrap };
        note.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var row = new DockPanel();
        DockPanel.SetDock(link, Dock.Right);
        row.Children.Add(link);
        row.Children.Add(note);
        var bar = new Border { Padding = new Thickness(16, 9, 16, 9), BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
        bar.SetResourceReference(Border.BackgroundProperty, "PreviewBrush");
        bar.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        Content = null;
        var panel = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        panel.Children.Add(bar);
        panel.Children.Add(text);
        Content = panel;
    }

    /// <summary>True when Windows marked the file as downloaded from the internet or an untrusted zone (ZoneId 3 or 4).</summary>
    internal static bool FromInternet(string path)
    {
        try
        {
            string? zone = File.ReadLines(path + ":Zone.Identifier").FirstOrDefault(l => l.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase));
            return zone != null && int.TryParse(zone.AsSpan(7), out int id) && id >= 3;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return false; }
    }

    /// <summary>
    /// Zooms the picture from its current size. It starts fitted to the pane (no scroll bars); zooming in gives it a
    /// fixed size and scroll bars, and zooming back down to the fitted size or below returns to fitting.
    /// </summary>
    public void Zoom(double factor)
    {
        if (_image?.Source is not BitmapSource bmp || _imageScroll == null || _image.ActualWidth <= 0) return;
        double width = Math.Min(_image.ActualWidth * factor, bmp.PixelWidth * 16);
        double fitted = FittedWidth(bmp);
        if (width <= fitted * 1.001) { ResetZoom(); return; }
        _image.Width = width;
        _image.Height = width * bmp.PixelHeight / bmp.PixelWidth;
        _image.StretchDirection = StretchDirection.Both;
        _imageScroll.HorizontalScrollBarVisibility = _imageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    public void ResetZoom()
    {
        if (_image == null || _imageScroll == null) return;
        _image.Width = _image.Height = double.NaN;
        _image.StretchDirection = StretchDirection.DownOnly;
        _imageScroll.HorizontalScrollBarVisibility = _imageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    /// <summary>The picture's width when fitted (never enlarged) into the pane.</summary>
    private double FittedWidth(BitmapSource bmp)
    {
        double w = _imageScroll!.ActualWidth, h = _imageScroll.ActualHeight;
        if (w <= 0 || h <= 0) return bmp.Width;
        return Math.Min(bmp.Width, Math.Min(w, h * bmp.PixelWidth / bmp.PixelHeight));
    }

    /// <summary>Stops media and frees WebView2 and preview handlers (called before every load and on unload).</summary>
    public void Release()
    {
        _web?.Dispose();
        _web = null;
        if (_media != null) { _media.Stop(); _media.Close(); _media = null; }
        _image = null;
        _imageScroll = null;
        if (Content is FrameworkElement { } old && old is Border { Child: PreviewHandlerHost host }) host.Dispose();
        Content = null;
    }

    private void SetDetails(string extra)
    {
        Details = extra.Length > 0 ? $"{_baseDetails} · {extra}" : _baseDetails;
        DetailsChanged?.Invoke();
    }

    private static string Describe(string path, bool isDir)
    {
        try
        {
            if (isDir) return Loc.F("Folder · modified {0:yyyy-MM-dd HH:mm}", Directory.GetLastWriteTime(path));
            var fi = new FileInfo(path);
            return Loc.F("{0} · modified {1:yyyy-MM-dd HH:mm}", FormatSize(fi.Length), fi.LastWriteTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

    // ---- Views ---------------------------------------------------------------------------------------

    private FrameworkElement? ImageView(string path)
    {
        BitmapImage bmp;
        try
        {
            bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException)
        {
            return null; // no WIC codec (e.g. HEIC without the extension): the thumbnail view takes over
        }
        ImageSize = new Size(bmp.PixelWidth, bmp.PixelHeight);
        SetDetails($"{bmp.PixelWidth} × {bmp.PixelHeight}");
        // Fitted to the pane at first: scroll bars disabled, so the picture gets the pane's size (never enlarged).
        var image = new Image { Source = bmp, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, ToolTip = Loc.T("Ctrl+wheel: zoom · Ctrl+0: fit") };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = image, Background = Checkerboard(), Focusable = false,
        };
        // The wheel scrolls (Shift+wheel sideways); Ctrl+wheel zooms.
        scroll.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; Zoom(e.Delta > 0 ? 1.15 : 1 / 1.15); }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta); }
        };
        _image = image;
        _imageScroll = scroll;
        return scroll;
    }

    private static Brush Checkerboard()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)), null, new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 8, 8)), new RectangleGeometry(new Rect(8, 8, 8, 8)) } }));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }

    private FrameworkElement WebView(string path, bool centredImage)
    {
        var host = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x24)) };
        var loading = new TextBlock { Text = Loc.T("Loading…"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };
        host.Children.Add(loading);
        var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x20, 0x22, 0x24) };
        _web = web;
        host.Children.Add(web);
        _ = InitWebAsync(web, path, centredImage, host, loading);
        return host;
    }

    private async Task InitWebAsync(WebView2 web, string path, bool centredImage, Grid host, TextBlock loading)
    {
        try
        {
            _webEnvironment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.DataRoot, "WebView2-preview"));
            var env = await _webEnvironment;
            await web.EnsureCoreWebView2Async(env);
            if (_web != web) return; // another file was chosen meanwhile
            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // Space and Esc reach the page first; let them close the preview (except Space in media, which plays/pauses).
            bool media = WebMediaExts.Contains(Path.GetExtension(path));
            core.WebMessageReceived += (_, e) => { if (e.TryGetWebMessageAsString() == "close") CloseRequested?.Invoke(); };
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "document.addEventListener('keydown', e => { if (e.key === 'Escape'" + (media ? "" : " || e.key === ' '") + ") { e.preventDefault(); window.chrome.webview.postMessage('close'); } }, true);");
            if (centredImage)
            {
                core.SetVirtualHostNameToFolderMapping("preview.chaodesk", Path.GetDirectoryName(path)!, CoreWebView2HostResourceAccessKind.Allow);
                string src = "https://preview.chaodesk/" + Uri.EscapeDataString(Path.GetFileName(path));
                core.NavigateToString($"<html><body style=\"margin:0;height:100vh;display:flex;align-items:center;justify-content:center;background:#202224\"><img src=\"{src}\" style=\"max-width:100%;max-height:100%\"></body></html>");
            }
            else core.Navigate(new Uri(path).AbsoluteUri);
            host.Children.Remove(loading);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or ArgumentException)
        {
            Log.Information(ex, "WebView2 preview failed for {Path}", path);
            if (_web == web) Content = InfoView(path, Loc.T("This preview needs the Microsoft Edge WebView2 Runtime."));
        }
    }

    /// <summary>Video the browser cannot play (MKV, AVI, WMV, …) through Windows' Media Foundation codecs.</summary>
    private FrameworkElement VideoView(string path)
    {
        var media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, Source = new Uri(path), Volume = 0.6 };
        _media = media;
        var state = new TextBlock { Foreground = Brushes.White, Margin = new Thickness(12, 8, 12, 8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Text = Loc.T("Click to pause") };
        var host = new Grid { Background = Brushes.Black, Cursor = Cursors.Hand };
        host.Children.Add(media);
        host.Children.Add(state);
        bool playing = true;
        host.MouseLeftButtonUp += (_, _) => { if (playing) media.Pause(); else media.Play(); playing = !playing; state.Text = playing ? Loc.T("Click to pause") : Loc.T("Paused — click to play"); };
        media.MediaOpened += (_, _) => { if (media.NaturalDuration.HasTimeSpan) SetDetails($"{media.NaturalVideoWidth} × {media.NaturalVideoHeight} · {media.NaturalDuration.TimeSpan:hh\\:mm\\:ss}"); };
        media.MediaFailed += (_, e) =>
        {
            Log.Information(e.ErrorException, "Media Foundation cannot play {Path}", path);
            if (_media == media) Content = InfoView(path, Loc.T("Windows has no codec to play this video. Press Enter to open it in your player."));
        };
        media.Play();
        return host;
    }

    private FrameworkElement HandlerView(string path, Guid clsid)
    {
        var host = new PreviewHandlerHost(path, clsid);
        host.Failed += _ => Dispatcher.BeginInvoke(() =>
        {
            if (CurrentPath != path) return;
            if (OfficeText.Extensions.Contains(Path.GetExtension(path))) ShowText(Loc.T("the Office preview could not open it — text only"));
            else Content = TextView(path) ?? InfoView(path, Loc.T("The installed preview for this kind of file could not open it."));
        });
        return new Border { Background = Brushes.White, Child = host };
    }

    private FrameworkElement? TextView(string path)
    {
        string? text;
        bool truncated;
        try { text = TextFiles.Read(path, out truncated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (text == null) return null;
        int lines = text.Count(c => c == '\n') + 1;
        SetDetails(truncated ? Loc.F("first 4 MB · {0:N0} lines", lines) : Loc.F("{0:N0} lines", lines));
        bool wrap = Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".md" or ".log" or ".srt" or ".lrc" or "";
        return TextBox(text, wrap);
    }

    private static TextBox TextBox(string text, bool wrap)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16, 12, 16, 12),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            FontSize = 13,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        box.SetResourceReference(BackgroundProperty, "CardBrush");
        box.SetResourceReference(ForegroundProperty, "InkBrush");
        return box;
    }

    private FrameworkElement FolderView(string path)
    {
        var entries = new List<(string Name, bool Dir, long Size)>();
        int dirs = 0, files = 0;
        try
        {
            foreach (var e in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                bool dir = (e.Attributes & FileAttributes.Directory) != 0;
                if (dir) dirs++; else files++;
                if (entries.Count < 1000) entries.Add((e.Name, dir, dir ? 0 : ((FileInfo)e).Length));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return InfoView(path, ex.Message); }
        SetDetails(Loc.F("{0:N0} folders, {1:N0} files", dirs, files));
        return Listing(entries.OrderByDescending(e => e.Dir).ThenBy(e => e.Name, Comparer<string>.Create((a, b) => StrCmpLogicalW(a, b))));
    }

    private FrameworkElement ZipView(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entries = zip.Entries.Where(e => e.Name.Length > 0).Take(2000).Select(e => (e.FullName, false, e.Length)).ToList();
            SetDetails(Loc.F("{0:N0} files inside, {1} unpacked", zip.Entries.Count(e => e.Name.Length > 0), FormatSize(zip.Entries.Sum(e => e.Length))));
            return Listing(entries.OrderBy(e => e.FullName, Comparer<string>.Create((a, b) => StrCmpLogicalW(a, b))));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return InfoView(path, Loc.T("Not a readable zip archive.")); }
    }

    private static FrameworkElement Listing(IEnumerable<(string Name, bool Dir, long Size)> entries)
    {
        var list = new ListBox { BorderThickness = new Thickness(0), Padding = new Thickness(8) };
        list.SetResourceReference(BackgroundProperty, "CardBrush");
        foreach (var (name, dir, size) in entries)
        {
            var row = new DockPanel { Margin = new Thickness(2, 1, 2, 1) };
            var sizeText = new TextBlock { Text = dir ? "" : FormatSize(size), Margin = new Thickness(16, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            sizeText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            DockPanel.SetDock(sizeText, Dock.Right);
            row.Children.Add(sizeText);
            row.Children.Add(new Image { Source = ShellIcons.ForExtension(dir ? "\\" : Path.GetExtension(name)), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            row.Children.Add(label);
            list.Items.Add(new ListBoxItem { Content = row });
        }
        return list;
    }

    /// <summary>A font file as a specimen in several sizes, Latin and Chinese.</summary>
    private FrameworkElement? FontView(string path)
    {
        try
        {
            var glyphs = new GlyphTypeface(new Uri(path));
            string family = glyphs.FamilyNames.TryGetValue(System.Globalization.CultureInfo.GetCultureInfo("en-US"), out var n) ? n : glyphs.FamilyNames.Values.FirstOrDefault() ?? "";
            var font = new FontFamily(new Uri(Path.GetDirectoryName(path)!.TrimEnd('\\') + "\\"), "./#" + family);
            SetDetails(Loc.F("{0} · {1:N0} glyphs", family, glyphs.GlyphCount));
            var stack = new StackPanel { Margin = new Thickness(24, 18, 24, 18) };
            void Line(string text, double size)
            {
                var t = new TextBlock { Text = text, FontFamily = font, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
                stack.Children.Add(t);
            }
            Line(family, 32);
            Line("ABCDEFGHIJKLMNOPQRSTUVWXYZ abcdefghijklmnopqrstuvwxyz 0123456789", 18);
            foreach (double size in new[] { 14.0, 20, 28, 40 }) Line("The quick brown fox jumps over the lazy dog. 天地玄黄，宇宙洪荒。", size);
            var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            scroll.SetResourceReference(BackgroundProperty, "CardBrush");
            return scroll;
        }
        catch (Exception ex) when (ex is IOException or FileFormatException or ArgumentException or UriFormatException) { return null; }
    }

    /// <summary>The shell's thumbnail (or icon) with the file's details.</summary>
    private FrameworkElement InfoView(string path, string? note = null)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16) };
        var thumb = ShellIcons.Thumbnail(path, 256) ?? ShellIcons.ForPath(path);
        stack.Children.Add(new Image { Source = thumb, MaxWidth = 256, MaxHeight = 256, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(0, 0, 0, 16) });
        var name = new TextBlock { Text = Path.GetFileName(path), FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 560 };
        name.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        stack.Children.Add(name);
        var lines = new List<string>();
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists) lines.Add(Loc.F("{0} · created {1:yyyy-MM-dd HH:mm} · modified {2:yyyy-MM-dd HH:mm}", FormatSize(fi.Length), fi.CreationTime, fi.LastWriteTime));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        lines.Add(note ?? Loc.T("No preview for this kind of file. Press Enter to open it."));

        foreach (string line in lines)
        {
            var t = new TextBlock { Text = line, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 560 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            stack.Children.Add(t);
        }
        return stack;
    }
}
