using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ChaoDesk.Host.Localization;

namespace ChaoDesk.Host.Preview;

/// <summary>
/// The text of a Word, PowerPoint or Excel (OOXML) file, read straight from its XML parts — the fallback when the
/// installed Office preview cannot show it. Nothing in the document runs (no macros, no external links).
/// </summary>
public static class OfficeText
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm", ".dotx", ".pptx", ".pptm", ".ppsx", ".xlsx", ".xlsm" };

    private const int MaxChars = 400_000;

    private static readonly XmlReaderSettings Settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    public static string? Extract(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            var text = ext.StartsWith(".doc") ? Word(zip) : ext.StartsWith(".pp") ? Slides(zip) : Sheets(zip);
            return text.Length > MaxChars ? text[..MaxChars] + "\n…" : text;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Paragraphs of word/document.xml (w:t runs, w:p paragraphs, w:tab, w:br).</summary>
    private static string Word(ZipArchive zip) =>
        zip.GetEntry("word/document.xml") is { } entry ? ReadRuns(entry.Open(), "t", "p", "tab", "br") : "";

    private static string Slides(ZipArchive zip)
    {
        var sb = new StringBuilder();
        var slides = zip.Entries
            .Select(e => (Entry: e, Match: Regex.Match(e.FullName, @"^ppt/slides/slide(\d+)\.xml$")))
            .Where(x => x.Match.Success)
            .OrderBy(x => int.Parse(x.Match.Groups[1].Value));
        foreach (var (entry, match) in slides)
        {
            sb.AppendLine(Loc.F("— Slide {0} —", match.Groups[1].Value));
            sb.AppendLine(ReadRuns(entry.Open(), "t", "p", null, "br").Trim());
            sb.AppendLine();
            if (sb.Length > MaxChars) break;
        }
        return sb.ToString();
    }

    /// <summary>The first worksheet as tab-separated rows (shared strings resolved), at most 1,000 rows.</summary>
    private static string Sheets(ZipArchive zip)
    {
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } sst)
        {
            var current = new StringBuilder();
            Walk(sst.Open(), (r, isText) =>
            {
                if (isText) current.Append(r.ReadElementContentAsString());
                else if (r.NodeType == XmlNodeType.Element && r.LocalName == "si") current.Clear();
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "si") shared.Add(current.ToString());
                return true;
            }, "t");
        }
        if (zip.GetEntry("xl/worksheets/sheet1.xml") is not { } sheet) return string.Join("\n", shared);
        var sb = new StringBuilder();
        int rows = 0;
        string? type = null;
        bool firstCell = true;
        Walk(sheet.Open(), (r, isValue) =>
        {
            if (isValue)
            {
                string value = r.ReadElementContentAsString();
                if (type == "s" && int.TryParse(value, out int i) && i < shared.Count) value = shared[i];
                if (!firstCell) sb.Append('\t');
                sb.Append(value);
                firstCell = false;
            }
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "row") firstCell = true;
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "c") type = r.GetAttribute("t");
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "row") { sb.AppendLine(); rows++; }
            return rows < 1000;
        }, "v", "t");
        return sb.ToString();
    }

    private static string ReadRuns(Stream xml, string text, string paragraph, string? tab, string? lineBreak)
    {
        var sb = new StringBuilder();
        Walk(xml, (r, isText) =>
        {
            if (isText) sb.Append(r.ReadElementContentAsString());
            else if (r.NodeType == XmlNodeType.Element && tab != null && r.LocalName == tab) sb.Append('\t');
            else if (r.NodeType == XmlNodeType.Element && lineBreak != null && r.LocalName == lineBreak) sb.Append('\n');
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == paragraph) sb.Append('\n');
            return sb.Length <= MaxChars;
        }, text);
        return sb.ToString();
    }

    /// <summary>
    /// Visits every node. For elements named in <paramref name="contentElements"/> the visitor gets true and must
    /// read the element's content (ReadElementContentAsString), which already moves the reader to the next node —
    /// so the walk must not call Read() again then, or that next node (often the next text run) would be skipped.
    /// </summary>
    private static void Walk(Stream xml, Func<XmlReader, bool, bool> visit, params string[] contentElements)
    {
        using var r = XmlReader.Create(xml, Settings);
        bool more = r.Read();
        while (more)
        {
            bool content = r.NodeType == XmlNodeType.Element && !r.IsEmptyElement && contentElements.Contains(r.LocalName);
            if (!visit(r, content)) return;
            more = content ? !r.EOF : r.Read();
        }
    }
}
