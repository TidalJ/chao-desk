namespace ChaoDesk.Host.Launcher;

/// <summary>
/// A name prepared for launcher matching. The name is split into tokens — each Chinese character (with its
/// pinyin readings) and each latin word (split at spaces, punctuation, camelCase and letter/digit changes) — and
/// a query matches when it can be spelled by taking a non-empty prefix of consecutive tokens. That one rule covers
/// initials ("wx" → 微信), full pinyin ("weixin"), mixtures ("weix", "微x") and word prefixes ("vsc", "vscode"
/// → Visual Studio Code). Plain substrings and, last, in-order letters also match, with lower scores.
/// </summary>
public sealed class SearchKey
{
    public const int ScoreExact = 1000;
    public const int ScorePrefix = 900;
    public const int ScoreWordSubstring = 800;
    public const int ScoreSubstring = 700;
    public const int ScoreTokensFromStart = 600;
    public const int ScoreTokens = 500;
    public const int ScoreSubsequence = 200;

    public string Text { get; }
    private readonly string _lower;
    private readonly string[][] _tokens;
    private readonly int[] _tokenStarts;

    public SearchKey(string text)
    {
        Text = text;
        _lower = text.ToLowerInvariant();
        var tokens = new List<string[]>();
        var starts = new List<int>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (Pinyin.IsHan(c))
            {
                var readings = Pinyin.Of(c);
                tokens.Add([.. readings, c.ToString()]);
                starts.Add(i);
                i++;
            }
            else if (char.IsLetterOrDigit(c))
            {
                int start = i++;
                while (i < text.Length && char.IsLetterOrDigit(text[i]) && !Pinyin.IsHan(text[i]) && !IsWordBreak(text[i - 1], text[i])) i++;
                tokens.Add([_lower[start..i]]);
                starts.Add(start);
            }
            else i++;
        }
        _tokens = tokens.ToArray();
        _tokenStarts = starts.ToArray();
    }

    private static bool IsWordBreak(char prev, char cur) =>
        (char.IsLower(prev) && char.IsUpper(cur)) || (char.IsDigit(prev) != char.IsDigit(cur));

    /// <summary>How well <paramref name="query"/> (already lower-case and trimmed) matches; 0 for no match.</summary>
    public int Match(string query)
    {
        if (query.Length == 0) return 0;
        if (_lower == query) return ScoreExact;
        if (_lower.StartsWith(query, StringComparison.Ordinal)) return ScorePrefix;
        int at = _lower.IndexOf(query, StringComparison.Ordinal);
        if (at > 0) return Array.IndexOf(_tokenStarts, at) >= 0 ? ScoreWordSubstring : ScoreSubstring;

        string compact = query.Replace(" ", "");
        if (compact.Length == 0) return 0;
        var memo = new byte[_tokens.Length + 1, compact.Length + 1];
        for (int t = 0; t < _tokens.Length; t++)
            if (MatchTokens(t, 0, compact, memo)) return t == 0 ? ScoreTokensFromStart : ScoreTokens;

        return compact.Length >= 2 && IsCompactSubsequence(compact, _lower) ? ScoreSubsequence : 0;
    }

    /// <summary>Can query[qi..] be spelled from prefixes of tokens t, t+1, …? (memo: 0 unknown, 1 yes, 2 no)</summary>
    private bool MatchTokens(int t, int qi, string q, byte[,] memo)
    {
        if (qi == q.Length) return true;
        if (t == _tokens.Length) return false;
        if (memo[t, qi] != 0) return memo[t, qi] == 1;
        bool ok = false;
        foreach (string reading in _tokens[t])
        {
            int common = 0;
            while (common < reading.Length && qi + common < q.Length && reading[common] == q[qi + common]) common++;
            for (int len = common; len >= 1 && !ok; len--) ok = MatchTokens(t + 1, qi + len, q, memo);
            if (ok) break;
        }
        memo[t, qi] = ok ? (byte)1 : (byte)2;
        return ok;
    }

    /// <summary>
    /// The query's letters appear in order within a short stretch of the name ("ntp" in "notepad"), not scattered
    /// across a long one ("pinyin" in "Intel Rapid Storage Technology Application").
    /// </summary>
    private static bool IsCompactSubsequence(string needle, string hay)
    {
        int maxWindow = needle.Length * 2 + 2;
        for (int start = hay.IndexOf(needle[0]); start >= 0; start = hay.IndexOf(needle[0], start + 1))
        {
            int j = 1, i = start + 1;
            for (; i < hay.Length && j < needle.Length && i - start < maxWindow; i++)
                if (hay[i] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }
}
