using System.IO;
using System.IO.Compression;
using ChaoDesk.Host.Localization;
using Serilog;

namespace ChaoDesk.Host.Library;

/// <summary>A wallpaper folder on disk plus its parsed manifest.</summary>
public sealed record WallpaperPackage(WallpaperManifest Manifest, string Folder, bool IsBuiltIn)
{
    public string Id => Manifest.Id;

    /// <summary>preview.png from the package, else a generated preview in the cache, else null.</summary>
    public string? PreviewPath
    {
        get
        {
            string own = Path.Combine(Folder, Manifest.Preview);
            if (File.Exists(own)) return own;
            string cached = WallpaperLibrary.GeneratedPreviewPath(Id);
            return File.Exists(cached) ? cached : null;
        }
    }

    public bool HasOwnPreview => File.Exists(Path.Combine(Folder, Manifest.Preview));
}

public sealed class ImportResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public WallpaperPackage? Package { get; init; }
}

/// <summary>
/// Scans the built-in wallpapers (next to the exe) and user-imported ones
/// (%LOCALAPPDATA%\ChaoDesk\wallpapers). User wallpapers with the same id override built-ins.
/// </summary>
public sealed class WallpaperLibrary
{
    private readonly string _builtInRoot;
    private readonly string _userRoot;
    private readonly bool _includeDiagnostic;
    private List<WallpaperPackage> _packages = new();

    public WallpaperLibrary(string builtInRoot, string userRoot, bool includeDiagnostic)
    {
        _builtInRoot = builtInRoot;
        _userRoot = userRoot;
        _includeDiagnostic = includeDiagnostic;
    }

    public IReadOnlyList<WallpaperPackage> Packages => _packages;

    /// <summary>Problems found during the last scan (folder → errors), for diagnostics.</summary>
    public IReadOnlyList<(string Folder, IReadOnlyList<string> Errors)> Problems { get; private set; } = [];

    public static string GeneratedPreviewPath(string id) => Path.Combine(AppPaths.Previews, $"{id}.png");

    private static void DeleteGeneratedPreview(string id)
    {
        string path = GeneratedPreviewPath(id);
        if (File.Exists(path)) File.Delete(path); // the previews folder may not exist yet
    }

    public void Scan()
    {
        var found = new Dictionary<string, WallpaperPackage>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<(string, IReadOnlyList<string>)>();
        foreach (var (root, builtIn) in new[] { (_builtInRoot, true), (_userRoot, false) })
        {
            if (!Directory.Exists(root)) continue;
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                if (!File.Exists(Path.Combine(dir, WallpaperManifest.FileName))) continue;
                var m = WallpaperManifest.Load(dir);
                if (m.Errors.Count > 0) problems.Add((dir, m.Errors));
                if (!m.IsUsable) continue;
                if (m.Diagnostic && !_includeDiagnostic) continue;
                found[m.Id] = new WallpaperPackage(m, dir, builtIn); // user folder scanned last → wins
            }
        }
        _packages = found.Values.OrderBy(p => p.Manifest.Diagnostic).ThenBy(p => p.Manifest.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Problems = problems;
        Log.Information("Wallpaper library: {Count} wallpaper(s): {Ids}", _packages.Count, string.Join(", ", _packages.Select(p => p.Id)));
        foreach (var (folder, errors) in problems)
            Log.Warning("Wallpaper at {Folder}: {Errors}", folder, string.Join("; ", errors));
    }

    public WallpaperPackage? Find(string id) =>
        _packages.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Validate a folder or .zip and copy it to the user wallpaper folder. A zip may hold the files at its
    /// root or inside a single top-level folder. Existing user wallpapers with the same id are replaced.
    /// </summary>
    public ImportResult Import(string source)
    {
        string? tempDir = null;
        try
        {
            string folder;
            if (File.Exists(source) && Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                tempDir = Path.Combine(Path.GetTempPath(), "ChaoDeskImport-" + Guid.NewGuid().ToString("N"));
                ExtractZipSafely(source, tempDir);
                folder = FindPackageRoot(tempDir) ?? tempDir;
            }
            else if (Directory.Exists(source))
            {
                folder = FindPackageRoot(source) ?? source;
            }
            else
            {
                return new ImportResult { Message = Loc.T("Choose a wallpaper folder or a .zip file.") };
            }

            var m = WallpaperManifest.Load(folder);
            if (!m.IsUsable)
                return new ImportResult { Message = Loc.T("This is not a valid wallpaper:") + "\n• " + string.Join("\n• ", m.Errors) };

            string target = Path.Combine(_userRoot, m.Id);
            if (Path.GetFullPath(folder).TrimEnd('\\').Equals(Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return new ImportResult { Message = Loc.T("That wallpaper is already in your library.") };

            Directory.CreateDirectory(_userRoot);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            CopyDirectory(folder, target);
            DeleteGeneratedPreview(m.Id); // stale generated preview

            Scan();
            var pkg = Find(m.Id);
            string warn = m.Errors.Count > 0 ? "\n\n" + Loc.T("Warnings:") + "\n• " + string.Join("\n• ", m.Errors) : "";
            Log.Information("Imported wallpaper {Id} from {Source}", m.Id, source);
            return new ImportResult { Success = true, Package = pkg, Message = Loc.F("Imported \"{0}\".", Loc.T(m.Name)) + warn };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Import failed for {Source}", source);
            return new ImportResult { Message = Loc.F("Import failed: {0}", ex.Message) };
        }
        finally
        {
            if (tempDir != null) { try { Directory.Delete(tempDir, true); } catch { /* best effort */ } }
        }
    }

    /// <summary>Delete a user-imported wallpaper (built-ins can't be removed).</summary>
    public bool Remove(string id)
    {
        var p = Find(id);
        if (p == null || p.IsBuiltIn) return false;
        try
        {
            Directory.Delete(p.Folder, recursive: true);
            DeleteGeneratedPreview(id);
            Scan();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not remove wallpaper {Id}", id);
            return false;
        }
    }

    /// <summary>The folder containing wallpaper.json: the folder itself or its single subfolder.</summary>
    internal static string? FindPackageRoot(string folder)
    {
        if (File.Exists(Path.Combine(folder, WallpaperManifest.FileName))) return folder;
        var subs = Directory.GetDirectories(folder);
        var files = Directory.GetFiles(folder);
        if (subs.Length == 1 && files.Length == 0 && File.Exists(Path.Combine(subs[0], WallpaperManifest.FileName))) return subs[0];
        return null;
    }

    /// <summary>Extract without allowing entries to escape the target folder ("zip slip").</summary>
    internal static void ExtractZipSafely(string zipPath, string target)
    {
        string root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            string dest = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsafe path in zip: {entry.FullName}");
            total += entry.Length;
            if (total > 1L << 30) throw new InvalidDataException("Zip is larger than 1 GB when extracted");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(dest); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (string dir in Directory.GetDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
