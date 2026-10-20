using System.Text;
using System.Windows;

namespace ChaoDesk.Host.Ocr;

/// <summary>A recognised word and where it is, in pixels of the original image.</summary>
public readonly record struct RecognizedWord(string Text, Rect Bounds);

/// <summary>One line of recognised words, left to right.</summary>
public sealed class RecognizedLine
{
    public RecognizedLine(IReadOnlyList<RecognizedWord> words)
    {
        Words = words;
        var r = Rect.Empty;
        foreach (var w in words) r.Union(w.Bounds);
        Bounds = r;
        var heights = words.Select(w => w.Bounds.Height).OrderBy(h => h).ToList();
        Height = heights.Count == 0 ? 0 : heights[heights.Count / 2];
    }

    public IReadOnlyList<RecognizedWord> Words { get; }
    public Rect Bounds { get; }

    /// <summary>The median word height: the line's text size.</summary>
    public double Height { get; }
}

/// <summary>Everything recognised in one image.</summary>
public sealed record RecognizedText(IReadOnlyList<RecognizedLine> Lines, int ImageWidth, int ImageHeight, string Engine, long Milliseconds)
{
    public bool IsEmpty => Lines.Count == 0;
    public static RecognizedText Empty(int width, int height, string engine) => new([], width, height, engine, 0);
}

/// <summary>Where one word landed in the composed text.</summary>
public readonly record struct WordSpan(int Line, int Word, int Start, int Length);

/// <summary>
/// Turns recognised words back into text the way it was written. Chinese and Japanese characters are words of their
/// own, so words are joined without spaces when either side is CJK (unless the gap between them is wide), and with
/// spaces between Latin words. Lines are kept, with a blank line where the layout leaves a paragraph gap — or merged
/// into paragraphs (hyphenated line ends rejoined).
/// </summary>
public static class OcrLayout
{
    public static bool IsCjk(char c) =>
        c is >= '⺀' and <= '鿿'      // radicals, CJK punctuation, kana, CJK ideographs
          or >= '가' and <= '힯'      // Hangul
          or >= '豈' and <= '﫿'      // compatibility ideographs
          or >= '＀' and <= '￯'      // full-width forms and punctuation
        || char.IsSurrogate(c);               // rare ideographs outside the BMP

    private static bool IsCjkPunctuation(char c) => c is >= '　' and <= '〿' or >= '＀' and <= '／' or >= '：' and <= '＠' or >= '［' and <= '｀' or >= '｛' and <= '･';

    /// <summary>Whether a space belongs between two neighbouring words of a line.</summary>
    public static bool NeedsSpace(RecognizedWord a, RecognizedWord b, double lineHeight)
    {
        if (a.Text.Length == 0 || b.Text.Length == 0) return false;
        char last = a.Text[^1], first = b.Text[0];
        double gap = b.Bounds.Left - a.Bounds.Right;
        double h = Math.Max(1, lineHeight);
        if (IsCjk(last) || IsCjk(first))
            return gap > h * 0.28 && !IsCjkPunctuation(last) && !IsCjkPunctuation(first); // a space is ~0.37 h; CJK characters sit ~0.05 h apart
        // Latin words: Windows splits them where the spaces are; pieces almost touching belong together, and so do
        // the pieces of a number or an address split at "." or "@" ("128." + "50", "a@b." + "com").
        // A sentence after a full stop starts with a capital; a decimal or domain continues in lower case or digits.
        if ("@/_".Contains(last) || char.IsLetterOrDigit(last) && "@".Contains(first)
            || (last is '.' or ':' or '-') && (char.IsLower(first) || char.IsDigit(first)) && a.Text.Length > 1 && char.IsLetterOrDigit(a.Text[^2]))
            return gap > h * 0.6;
        if (char.IsLetterOrDigit(last) && first == '.') return gap > h * 0.35;
        return gap > h * 0.12;
    }

    /// <summary>A line's words as text.</summary>
    public static string LineText(RecognizedLine line, Func<int, bool>? include = null)
    {
        var sb = new StringBuilder();
        RecognizedWord? previous = null;
        for (int i = 0; i < line.Words.Count; i++)
        {
            if (include != null && !include(i)) { previous = null; continue; }
            var w = line.Words[i];
            if (previous is { } p && NeedsSpace(p, w, line.Height)) sb.Append(' ');
            sb.Append(w.Text);
            previous = w;
        }
        return sb.ToString();
    }

    /// <summary>
    /// The whole text, and where each word is in it (so selecting words on the picture can select them in the text).
    /// </summary>
    public static (string Text, List<WordSpan> Spans) Compose(RecognizedText result, bool mergeLines)
    {
        var sb = new StringBuilder();
        var spans = new List<WordSpan>();
        var lines = result.Lines;
        double typical = lines.Count == 0 ? 0 : lines.Select(l => l.Height).OrderBy(h => h).ElementAt(lines.Count / 2);
        double blockLeft = lines.Count == 0 ? 0 : lines.Min(l => l.Bounds.Left);
        double blockRight = lines.Count == 0 ? 0 : lines.Max(l => l.Bounds.Right);
        for (int li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            if (li > 0)
            {
                var prev = lines[li - 1];
                double gap = line.Bounds.Top - prev.Bounds.Bottom;
                string prevText = sb.Length > 0 ? sb.ToString(Math.Max(0, sb.Length - 2), Math.Min(2, sb.Length)) : "";
                if (mergeLines)
                {
                    bool newParagraph = gap > typical * 0.75 || StartsListItem(line) || EndsEarly(prev, blockRight, typical) && EndsSentence(prevText)
                                        || line.Bounds.Left > blockLeft + typical * 1.5 && prev.Bounds.Left <= blockLeft + typical * 0.5; // an indented first line
                    if (newParagraph) sb.Append('\n');
                    else JoinAcross(sb, line);
                }
                else
                {
                    sb.Append('\n');
                    if (gap > typical * 1.0) sb.Append('\n'); // a paragraph gap in the layout
                }
            }
            RecognizedWord? previous = null;
            for (int wi = 0; wi < line.Words.Count; wi++)
            {
                var w = line.Words[wi];
                if (previous is { } p && NeedsSpace(p, w, line.Height)) sb.Append(' ');
                spans.Add(new WordSpan(li, wi, sb.Length, w.Text.Length));
                sb.Append(w.Text);
                previous = w;
            }
        }
        return (sb.ToString(), spans);
    }

    /// <summary>Joins a wrapped line onto the previous one: no space for CJK, a rejoined hyphenated word, else a space.</summary>
    private static void JoinAcross(StringBuilder sb, RecognizedLine next)
    {
        if (sb.Length == 0 || next.Words.Count == 0) return;
        char last = sb[^1], first = next.Words[0].Text[0];
        if (IsCjk(last) || IsCjk(first)) return;
        if (last == '-' && sb.Length > 1 && char.IsLetter(sb[^2]) && char.IsLower(first)) { sb.Length--; return; }
        sb.Append(' ');
    }

    private static bool EndsEarly(RecognizedLine line, double blockRight, double typical) => line.Bounds.Right < blockRight - typical * 2;

    private static bool EndsSentence(string tail) => tail.Length > 0 && ".!?:;。！？：；…".Contains(tail[^1]);

    private static bool StartsListItem(RecognizedLine line)
    {
        if (line.Words.Count == 0) return false;
        string first = line.Words[0].Text;
        return first is "•" or "·" or "-" or "–" or "—" or "*" or "○" or "●" or "■" or "□"
               || first.Length <= 4 && first.Length >= 2 && char.IsDigit(first[0]) && first[^1] is '.' or ')' or '、' or '）';
    }

    /// <summary>The selected words as text: per line in reading order, lines on separate lines.</summary>
    public static string SelectionText(RecognizedText result, ISet<(int Line, int Word)> selected)
    {
        var parts = new List<string>();
        for (int li = 0; li < result.Lines.Count; li++)
        {
            int index = li;
            if (!result.Lines[li].Words.Where((_, wi) => selected.Contains((index, wi))).Any()) continue;
            parts.Add(LineText(result.Lines[li], wi => selected.Contains((index, wi))));
        }
        return string.Join("\n", parts);
    }
}
