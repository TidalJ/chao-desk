using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using ChaoDesk.Host.Library;

namespace ChaoDesk.Host.Tests;

public class WallpaperManifestTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "host", "ChaoDesk.sln"))) dir = dir.Parent;
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    public void ParsesTheLotusPondManifest()
    {
        var m = WallpaperManifest.Parse(File.ReadAllText(RepoFile("wallpapers", "lotus-pond", "wallpaper.json")));
        Assert.Empty(m.Errors);
        Assert.True(m.IsUsable);
        Assert.Equal("lotus-pond", m.Id);
        Assert.Equal("Lotus Pond", m.Name);
        Assert.Equal("#2c4038", m.Background);
        Assert.Equal(["Celadon basin", "Wisteria garden", "Summer lotus", "Autumn leaves"], m.Properties.Single(p => p.Key == "scene").Options);
        var speed = m.Properties.Single(p => p.Key == "speed");
        Assert.Equal(PropertyType.Slider, speed.Type);
        Assert.Equal(0.3, speed.Min);
        Assert.Equal(2, speed.Max);
        Assert.Equal(1, speed.Default.GetValue<double>());
        Assert.Equal(["Follow real weather", "Sunny", "Cloudy", "Rain", "Snow"], m.Properties.Single(p => p.Key == "weather").Options);
    }

    [Fact]
    public void InvalidManifest_ReportsErrors_NeverThrows()
    {
        var m = WallpaperManifest.Parse("""
        { "id": "bad id!", "properties": { "a": { "type": "wheel" }, "b": { "type": "slider", "min": 5, "max": 1 },
          "c": { "type": "color", "default": "red" } } }
        """);
        Assert.False(m.IsUsable);
        Assert.Contains(m.Errors, e => e.Contains("\"id\""));
        Assert.Contains(m.Errors, e => e.Contains("\"name\""));
        Assert.Contains(m.Errors, e => e.Contains("unknown type"));
        Assert.Contains(m.Errors, e => e.Contains("min < max"));
        Assert.Contains(m.Errors, e => e.Contains("#rrggbb"));

        Assert.False(WallpaperManifest.Parse("{ not json").IsUsable);
        Assert.False(WallpaperManifest.Parse("[1,2]").IsUsable);
    }

    [Fact]
    public void RejectsEntryOutsideThePackage()
    {
        var m = WallpaperManifest.Parse("""{ "id": "x", "name": "X", "entry": "../../evil.html" }""");
        Assert.Equal("index.html", m.Entry);
        Assert.Contains(m.Errors, e => e.Contains("entry"));
    }

    [Fact]
    public void MergeProperties_CoercesStoredValues()
    {
        var m = WallpaperManifest.Parse(File.ReadAllText(RepoFile("wallpapers", "lotus-pond", "wallpaper.json")));
        var stored = new Dictionary<string, JsonNode?>
        {
            ["speed"] = JsonValue.Create(99),
            ["fireflies"] = JsonValue.Create("yes"),
            ["weather"] = JsonValue.Create("Rain"),
            ["timeOfDay"] = JsonValue.Create("Sometimes"),
            ["unknown"] = JsonValue.Create(1),
        };
        var merged = m.MergeProperties(stored);
        Assert.Equal(2, merged["speed"]!.GetValue<double>());
        Assert.True(merged["fireflies"]!.GetValue<bool>());
        Assert.Equal("Rain", merged["weather"]!.GetValue<string>());
        Assert.Equal("Follow clock", merged["timeOfDay"]!.GetValue<string>());
        Assert.False(merged.ContainsKey("unknown"));
    }

    [Fact]
    public void SliderSnapsToStep()
    {
        var m = WallpaperManifest.Parse("""{ "id": "x", "name": "X", "properties": { "s": { "type": "slider", "min": 0, "max": 10, "step": 2, "default": 0 } } }""");
        Assert.Equal(6, m.Properties[0].Coerce(JsonValue.Create(5.2)).GetValue<double>());
    }
}

public sealed class WallpaperLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lp-lib-" + Guid.NewGuid().ToString("N"));
    private string BuiltIn => Path.Combine(_root, "builtin");
    private string User => Path.Combine(_root, "user");

    public WallpaperLibraryTests()
    {
        Directory.CreateDirectory(BuiltIn);
        Directory.CreateDirectory(User);
        MakePackage(Path.Combine(BuiltIn, "alpha"), "alpha", "Alpha");
        MakePackage(Path.Combine(BuiltIn, "diag"), "diag", "Diag", diagnostic: true);
        Directory.CreateDirectory(Path.Combine(BuiltIn, "broken"));
        File.WriteAllText(Path.Combine(BuiltIn, "broken", "wallpaper.json"), "{ nope");
    }

    private static void MakePackage(string dir, string id, string name, bool diagnostic = false)
    {
        Directory.CreateDirectory(Path.Combine(dir, "assets"));
        File.WriteAllText(Path.Combine(dir, "wallpaper.json"),
            $$"""{ "id": "{{id}}", "name": "{{name}}", "entry": "index.html", "diagnostic": {{(diagnostic ? "true" : "false")}} }""");
        File.WriteAllText(Path.Combine(dir, "index.html"), "<!doctype html>");
        File.WriteAllText(Path.Combine(dir, "assets", "a.txt"), "x");
    }

    [Fact]
    public void Scan_FindsValidPackages_ReportsBroken_HidesDiagnostic()
    {
        var lib = new WallpaperLibrary(BuiltIn, User, includeDiagnostic: false);
        lib.Scan();
        Assert.Equal(["alpha"], lib.Packages.Select(p => p.Id));
        Assert.Contains(lib.Problems, p => p.Folder.EndsWith("broken"));

        var dev = new WallpaperLibrary(BuiltIn, User, includeDiagnostic: true);
        dev.Scan();
        Assert.Equal(2, dev.Packages.Count);
    }

    [Fact]
    public void ImportFolder_CopiesToUserFolder()
    {
        string src = Path.Combine(_root, "incoming", "beta");
        MakePackage(src, "beta", "Beta");
        var lib = new WallpaperLibrary(BuiltIn, User, false);
        lib.Scan();
        var r = lib.Import(src);
        Assert.True(r.Success, r.Message);
        Assert.True(File.Exists(Path.Combine(User, "beta", "assets", "a.txt")));
        Assert.NotNull(lib.Find("beta"));
        Assert.False(lib.Find("beta")!.IsBuiltIn);
    }

    [Fact]
    public void ImportZip_WithTopLevelFolder()
    {
        string src = Path.Combine(_root, "zipsrc", "gamma");
        MakePackage(src, "gamma", "Gamma");
        string zip = Path.Combine(_root, "gamma.zip");
        ZipFile.CreateFromDirectory(Path.GetDirectoryName(src)!, zip); // zip contains gamma/...
        var lib = new WallpaperLibrary(BuiltIn, User, false);
        var r = lib.Import(zip);
        Assert.True(r.Success, r.Message);
        Assert.True(File.Exists(Path.Combine(User, "gamma", "index.html")));
    }

    [Fact]
    public void ImportZip_RejectsPathTraversal()
    {
        string zip = Path.Combine(_root, "evil.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("wallpaper.json").Open())) w.Write("""{ "id": "evil", "name": "Evil" }""");
            using (var w = new StreamWriter(z.CreateEntry("../../outside.txt").Open())) w.Write("x");
        }
        var lib = new WallpaperLibrary(BuiltIn, User, false);
        var r = lib.Import(zip);
        Assert.False(r.Success);
        Assert.Contains("Unsafe path", r.Message);
        Assert.False(Directory.Exists(Path.Combine(User, "evil")));
    }

    [Fact]
    public void ImportInvalid_ExplainsWhy()
    {
        string src = Path.Combine(_root, "incoming", "noentry");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "wallpaper.json"), """{ "id": "noentry", "name": "No entry" }""");
        var r = new WallpaperLibrary(BuiltIn, User, false).Import(src);
        Assert.False(r.Success);
        Assert.Contains("index.html", r.Message);
    }

    [Fact]
    public void UserWallpaperOverridesBuiltInWithSameId_AndCanBeRemoved()
    {
        MakePackage(Path.Combine(User, "alpha"), "alpha", "Alpha (mine)");
        var lib = new WallpaperLibrary(BuiltIn, User, false);
        lib.Scan();
        Assert.Equal("Alpha (mine)", lib.Find("alpha")!.Manifest.Name);
        Assert.True(lib.Remove("alpha"));
        Assert.Equal("Alpha", lib.Find("alpha")!.Manifest.Name);
        Assert.False(lib.Remove("alpha")); // built-in can't be removed
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }
}
