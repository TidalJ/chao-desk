using System.IO;
using System.Text.RegularExpressions;
using ChaoDesk.Host.Localization;

namespace ChaoDesk.Host.Tests;

/// <summary>
/// Every interface string has a Chinese entry: literal text in XAML attributes, and every Loc.T / Loc.F in
/// code. Entries nothing refers to any more fail too, except those in Localization/zh/dynamic*.txt (text that reaches
/// Loc.T through a variable, e.g. manifest labels).
/// </summary>
public class LocalizationTests
{
    private static readonly string HostSource = Path.Combine(RepoRoot(), "host", "src", "ChaoDesk.Host");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "host", "ChaoDesk.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root");
    }

    private static readonly Regex XamlText = new(
        "\\s(?:Text|Content|Header|Title|ToolTip)=\"(?<v>[^\"{][^\"]*)\"", RegexOptions.Compiled);

    private static readonly Regex CodeText = new(
        "Loc\\.(?:T|F)\\(\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(HostSource, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>Every key the source uses, with the file it is in.</summary>
    internal static List<(string Key, string File)> UsedKeys()
    {
        var keys = new List<(string, string)>();
        foreach (var file in SourceFiles("*.xaml"))
            foreach (Match m in XamlText.Matches(File.ReadAllText(file)))
            {
                string v = System.Net.WebUtility.HtmlDecode(m.Groups["v"].Value);
                if (IsWords(v)) keys.Add((v, Path.GetRelativePath(HostSource, file)));
            }
        foreach (var file in SourceFiles("*.cs"))
            foreach (Match m in CodeText.Matches(File.ReadAllText(file)))
                keys.Add((Regex.Unescape(m.Groups["v"].Value), Path.GetRelativePath(HostSource, file)));
        return keys;
    }

    /// <summary>Text a person reads: has a latin letter and is not a glyph, a key name or a font.</summary>
    private static bool IsWords(string v) =>
        v.Any(char.IsAsciiLetter) && !v.All(c => c > '\ue000' || char.IsWhiteSpace(c)) &&
        !Regex.IsMatch(v, "^(Ctrl|Alt|Shift|Win|Esc|Enter|Space|Tab|Del|F\\d+)([+ ].*)?$") && v != "Chao Desk";

    [Fact]
    public void EveryInterfaceStringHasChinese()
    {
        var missing = UsedKeys().Where(k => !Loc.Chinese.ContainsKey(k.Key))
            .GroupBy(k => k.File).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}:\n" + string.Join("\n", g.Select(k => "    " + k.Key.Replace("\n", "\\n")).Distinct()))
            .ToList();
        Assert.True(missing.Count == 0, "Missing Chinese (add to host/src/ChaoDesk.Host/Localization/zh/*.txt):\n" + string.Join("\n", missing));
    }

    [Fact]
    public void NoUnusedEntries()
    {
        var used = UsedKeys().Select(k => k.Key).ToHashSet();
        var dynamicKeys = Directory.EnumerateFiles(Path.Combine(HostSource, "Localization", "zh"), "dynamic*.txt")
            .SelectMany(f => Loc.Parse(File.ReadAllText(f))).Select(p => p.English).ToHashSet();
        var unused = Loc.Chinese.Keys.Where(k => !used.Contains(k) && !dynamicKeys.Contains(k)).ToList();
        Assert.True(unused.Count == 0, "Entries nothing uses (remove them, or move them to a dynamic*.txt):\n" + string.Join("\n", unused));
    }

    [Fact]
    public void PlaceholdersMatch()
    {
        var wrong = Loc.Chinese.Where(kv => Placeholders(kv.Key) != Placeholders(kv.Value)).Select(kv => $"{kv.Key} → {kv.Value}").ToList();
        Assert.True(wrong.Count == 0, "Placeholders differ:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void TableFilesHaveNoConflicts()
    {
        var seen = new Dictionary<string, (string Value, string File)>();
        var conflicts = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(HostSource, "Localization", "zh"), "*.txt"))
            foreach (var (en, zh) in Loc.Parse(File.ReadAllText(file)))
            {
                if (seen.TryGetValue(en, out var other) && other.Value != zh)
                    conflicts.Add($"{en}: \"{other.Value}\" ({Path.GetFileName(other.File)}) vs \"{zh}\" ({Path.GetFileName(file)})");
                seen[en] = (zh, file);
            }
        Assert.True(conflicts.Count == 0, string.Join("\n", conflicts));
    }

    [Fact]
    public void ParsesTables()
    {
        var pairs = Loc.Parse("# comment\n\nCopy\t复制\r\nTwo\\nlines\t两\\n行\nbroken line\n").ToList();
        Assert.Equal([("Copy", "复制"), ("Two\nlines", "两\n行")], pairs);
    }

    private static string Placeholders(string s) => string.Join(",", Regex.Matches(s, "\\{\\d+[^}]*\\}").Select(m => m.Value).Order());
}
