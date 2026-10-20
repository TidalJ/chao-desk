using System.Text.RegularExpressions;

namespace ChaoDesk.Host.Files;

/// <summary>
/// A parsed file search, a small subset of Everything's syntax:
/// <c>report 2024</c> (all words in the name), <c>ext:mp4;mkv</c> or <c>*.mp4</c> (extension), <c>a*b?.txt</c>
/// (wildcards over the whole name), <c>docs\report</c> (a term with a backslash matches the full path),
/// <c>folder:</c> / <c>file:</c>.
/// </summary>
public sealed class FileQuery
{
    public IReadOnlyList<string> NameTerms { get; }
    public IReadOnlyList<string> PathTerms { get; }
    public IReadOnlyList<string> Extensions { get; }
    public Regex? Wildcard { get; }

    /// <summary>The longest run of literal characters in the wildcard ("report" in "report*.docx"), for the index scan.</summary>
    public string? WildcardLiteral { get; private init; }

    public bool FoldersOnly { get; }
    public bool FilesOnly { get; }

    public bool IsEmpty => NameTerms.Count == 0 && PathTerms.Count == 0 && Extensions.Count == 0 && Wildcard == null && !FoldersOnly && !FilesOnly;

    private FileQuery(List<string> names, List<string> paths, List<string> exts, Regex? wildcard, bool folders, bool files)
    {
        // The longest name term is the most selective; scan with it first.
        NameTerms = names.OrderByDescending(t => t.Length).ToList();
        PathTerms = paths;
        Extensions = exts;
        Wildcard = wildcard;
        FoldersOnly = folders;
        FilesOnly = files;
    }

    public static FileQuery Parse(string text)
    {
        var names = new List<string>();
        var paths = new List<string>();
        var exts = new List<string>();
        Regex? wildcard = null;
        string? literal = null;
        bool folders = false, files = false;
        foreach (string raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string t = raw.Trim('"');
            if (t.Length == 0) continue;
            string lower = t.ToLowerInvariant();
            if (lower == "folder:") { folders = true; continue; }
            if (lower == "file:") { files = true; continue; }
            if (lower.StartsWith("ext:"))
            {
                exts.AddRange(lower[4..].Split(';', ',', StringSplitOptions.RemoveEmptyEntries).Select(e => e.TrimStart('.')));
                continue;
            }
            var extOnly = Regex.Match(lower, @"^\*\.([^.*?\\/]+)$");
            if (extOnly.Success) { exts.Add(extOnly.Groups[1].Value); continue; }
            if (t.Contains('*') || t.Contains('?'))
            {
                if (wildcard == null)
                {
                    string pattern = "^" + Regex.Escape(t).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                    wildcard = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    literal = t.Split('*', '?').OrderByDescending(p => p.Length).First();
                }
                continue;
            }
            if (t.Contains('\\') || t.Contains('/')) { paths.Add(t.Replace('/', '\\')); continue; }
            names.Add(t);
        }
        return new FileQuery(names, paths, exts, wildcard, folders, files) { WildcardLiteral = literal };
    }

    /// <summary>Does the extension of <paramref name="name"/> match the ext: filter (true when there is none)?</summary>
    public bool ExtensionMatches(ReadOnlySpan<char> name)
    {
        if (Extensions.Count == 0) return true;
        int dot = name.LastIndexOf('.');
        if (dot < 0) return false;
        var ext = name[(dot + 1)..];
        foreach (string e in Extensions)
            if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
