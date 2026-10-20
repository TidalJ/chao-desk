using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChaoDesk.Host.Desktop;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Launcher;

/// <summary>An app from the Start menu (desktop or Store app).</summary>
public sealed class AppEntry
{
    public required string Name { get; init; }

    /// <summary>The AppsFolder id: an AppUserModelID or a known-folder path such as "{6D80…}\7-Zip\7zFM.exe".</summary>
    public required string ParsingName { get; init; }

    /// <summary>The executable or document on disk, when there is one (desktop apps).</summary>
    public string? Path { get; init; }

    public required SearchKey Key { get; init; }

    /// <summary>
    /// A second name to match: the executable ("WeChat", "Code") or Store package ("WindowsNotepad"), so English
    /// queries find apps whose Start menu name is localised (notepad → 记事本, calc → 计算器).
    /// </summary>
    public SearchKey? Alias { get; init; }

    public ImageSource? Icon { get; set; }
    public string Id => "app:" + ParsingName;

    public static SearchKey? AliasFor(string name, string parsingName, string? path)
    {
        string alias;
        if (path != null) alias = System.IO.Path.GetFileNameWithoutExtension(path);
        else
        {
            int cut = parsingName.IndexOfAny(['_', '!']);
            alias = cut > 0 ? parsingName[..cut] : parsingName;
            if (alias.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)) alias = alias["Microsoft.".Length..];
        }
        return alias.Length == 0 || string.Equals(alias, name, StringComparison.OrdinalIgnoreCase) ? null : new SearchKey(alias);
    }
}

/// <summary>
/// Everything the Start menu lists, read from the shell's AppsFolder (the same list as Start → All apps:
/// desktop apps from both Start Menu folders and Store apps, with their localised names and icons). Enumerated
/// on a dedicated STA thread; refreshed in the background when the launcher opens and the list is stale.
/// </summary>
public sealed class AppCatalog : IDisposable
{
    private static readonly string[] SkippedExtensions = [".txt", ".chm", ".pdf", ".htm", ".html", ".rtf", ".md", ".log", ".ini"];

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly ConcurrentDictionary<string, ImageSource?> _icons = new();
    private volatile IReadOnlyList<AppEntry> _apps = [];
    private long _refreshedAt = long.MinValue;
    private int _refreshing;
    private string _savedSignature = "";

    public IReadOnlyList<AppEntry> Apps => _apps;

    /// <summary>
    /// Where the last list (names, paths and icons) is kept: after a restart the launcher has every app at once, and
    /// the refresh only extracts icons for apps that are new.
    /// </summary>
    public string? CachePath { get; init; }

    /// <summary>Raised on the catalog thread after a refresh.</summary>
    public event Action? Changed;

    public AppCatalog()
    {
        _thread = new Thread(() => { foreach (var work in _queue.GetConsumingEnumerable()) work(); })
        {
            IsBackground = true,
            Name = "ChaoDesk.AppCatalog",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void RefreshIfOlderThan(TimeSpan age)
    {
        if (Environment.TickCount64 - _refreshedAt < age.TotalMilliseconds) return;
        Refresh();
    }

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1 || _queue.IsAddingCompleted) return;
        _queue.Add(() =>
        {
            try
            {
                if (_refreshedAt == long.MinValue && _apps.Count == 0) LoadCache();
                var started = Stopwatch.StartNew();
                Enumerate();
                _refreshedAt = Environment.TickCount64;
                Log.Information("App catalog: {Count} apps in {Ms} ms", _apps.Count, started.ElapsedMilliseconds);
                SaveCache();
            }
            catch (Exception ex) { Log.Warning(ex, "Could not list the Start menu apps"); }
            finally { Volatile.Write(ref _refreshing, 0); }
        });
    }

    /// <summary>Runs <paramref name="work"/> on the catalog's STA thread (shell calls such as file icons).</summary>
    public void Post(Action work)
    {
        if (!_queue.IsAddingCompleted) _queue.Add(work);
    }

    /// <summary>
    /// Lists the apps and publishes them at once (names are quick), then extracts the icons that are not cached
    /// yet (slow the first time: several seconds for a few hundred apps) and publishes again.
    /// </summary>
    private void Enumerate()
    {
        var result = new List<AppEntry>();
        var needIcons = new List<(AppEntry App, ShellInterop.IShellItem Item)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folder = (ShellInterop.IShellItem)SHGetKnownFolderItem(ShellInterop.FOLDERID_AppsFolder, 0, IntPtr.Zero, ShellInterop.IID_IShellItem);
        var items = (ShellInterop.IEnumShellItems)folder.BindToHandler(IntPtr.Zero, ShellInterop.BHID_EnumItems, ShellInterop.IID_IEnumShellItems);
        try
        {
            while (items.Next(1, out var item, out uint fetched) == 0 && fetched == 1 && item != null)
            {
                bool keep = false;
                try
                {
                    string? name = ShellInterop.DisplayName(item, ShellInterop.SIGDN.NORMALDISPLAY);
                    string? parsing = ShellInterop.DisplayName(item, ShellInterop.SIGDN.PARENTRELATIVEPARSING);
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parsing) || !seen.Add(parsing)) continue;
                    string? path = ResolvePath(parsing);
                    if (IsClutter(name, path)) continue;
                    var app = new AppEntry { Name = name, ParsingName = parsing, Path = path, Key = new SearchKey(name), Alias = AppEntry.AliasFor(name, parsing, path) };
                    if (_icons.TryGetValue(parsing, out var icon)) app.Icon = icon;
                    else { needIcons.Add((app, item)); keep = true; }
                    result.Add(app);
                }
                finally { if (!keep) Marshal.ReleaseComObject(item); }
            }
            _apps = result;
            Changed?.Invoke();

            foreach (var (app, item) in needIcons)
            {
                try { app.Icon = _icons.GetOrAdd(app.ParsingName, _ => ShellIcons.ForShellItem(item)); }
                catch (COMException) { }
                finally { Marshal.ReleaseComObject(item); }
            }
            if (needIcons.Count > 0) Changed?.Invoke();
        }
        finally
        {
            Marshal.ReleaseComObject(items);
            Marshal.ReleaseComObject(folder);
        }
    }

    // ---- Cache ------------------------------------------------------------------------------------------

    private const int CacheMagic = 0x50414443; // "CDAP"

    private string Signature() => string.Join("|", _apps.Select(a => a.ParsingName + (a.Icon != null ? "+" : "")));

    private void LoadCache()
    {
        if (CachePath == null || !File.Exists(CachePath)) return;
        try
        {
            var list = new List<AppEntry>();
            using (var r = new BinaryReader(File.OpenRead(CachePath)))
            {
                if (r.ReadInt32() != CacheMagic) return;
                int count = r.ReadInt32();
                for (int i = 0; i < count && i < 5000; i++)
                {
                    string name = r.ReadString(), parsing = r.ReadString(), path = r.ReadString();
                    byte[] png = r.ReadBytes(r.ReadInt32());
                    ImageSource? icon = null;
                    if (png.Length > 0)
                    {
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.StreamSource = new MemoryStream(png);
                        image.EndInit();
                        image.Freeze();
                        icon = image;
                        _icons[parsing] = icon;
                    }
                    string? p = path.Length > 0 ? path : null;
                    list.Add(new AppEntry { Name = name, ParsingName = parsing, Path = p, Key = new SearchKey(name), Alias = AppEntry.AliasFor(name, parsing, p), Icon = icon });
                }
            }
            _apps = list;
            _savedSignature = Signature();
            Changed?.Invoke();
            Log.Information("App catalog: {Count} apps from the cache", list.Count);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or NotSupportedException or FileFormatException or UnauthorizedAccessException)
        {
            Log.Information(ex, "Ignoring the app cache");
        }
    }

    private void SaveCache()
    {
        string signature = Signature();
        if (CachePath == null || signature == _savedSignature) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            string tmp = CachePath + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                var apps = _apps;
                w.Write(CacheMagic);
                w.Write(apps.Count);
                foreach (var app in apps)
                {
                    w.Write(app.Name);
                    w.Write(app.ParsingName);
                    w.Write(app.Path ?? "");
                    byte[] png = [];
                    if (app.Icon is BitmapSource bitmap)
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var ms = new MemoryStream();
                        encoder.Save(ms);
                        png = ms.ToArray();
                    }
                    w.Write(png.Length);
                    w.Write(png);
                }
            }
            File.Move(tmp, CachePath, overwrite: true);
            _savedSignature = signature;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Information(ex, "Could not save the app cache");
        }
    }

    /// <summary>Uninstallers and read-me documents clutter results; skip them.</summary>
    internal static bool IsClutter(string name, string? path)
    {
        if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("卸载")) return true;
        if (path == null) return false;
        string file = System.IO.Path.GetFileName(path);
        if (file.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || file.StartsWith("uninst", StringComparison.OrdinalIgnoreCase)) return true;
        return SkippedExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"{known-folder-guid}\rest" or "C:\…" → a file system path; Store apps (AUMIDs) → null.</summary>
    internal static string? ResolvePath(string parsing)
    {
        if (parsing.Length > 2 && parsing[1] == ':' && parsing[2] == '\\') return parsing;
        if (parsing.Length < 39 || parsing[0] != '{' || parsing[37] != '}') return null;
        if (!Guid.TryParse(parsing.AsSpan(1, 36), out var guid)) return null;
        if (SHGetKnownFolderPath(guid, 0, IntPtr.Zero, out IntPtr p) != 0) return null;
        try
        {
            string root = Marshal.PtrToStringUni(p) ?? "";
            return root.Length == 0 ? null : System.IO.Path.Combine(root, parsing[38..].TrimStart('\\'));
        }
        finally { CoTaskMemFree(p); }
    }

    /// <summary>Starts an app the way Start does (through its AppsFolder entry), or elevated when asked.</summary>
    public static void Launch(AppEntry app, bool asAdmin)
    {
        AllowSetForegroundWindow(-1); // ASFW_ANY: let the new app take the foreground from us
        if (asAdmin && app.Path != null && File.Exists(app.Path))
        {
            Process.Start(new ProcessStartInfo(app.Path)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = System.IO.Path.GetDirectoryName(app.Path) ?? "",
            });
            return;
        }
        Process.Start(new ProcessStartInfo("shell:AppsFolder\\" + app.ParsingName) { UseShellExecute = true });
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(2000);
    }
}
