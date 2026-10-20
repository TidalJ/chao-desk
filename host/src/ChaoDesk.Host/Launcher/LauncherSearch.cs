using System.Text.RegularExpressions;

namespace ChaoDesk.Host.Launcher;

/// <summary>Ranking of apps and commands for a query (pure; unit-tested).</summary>
public static class LauncherSearch
{
    /// <summary>A result. <paramref name="Match"/> is the match quality alone; <paramref name="Score"/> adds usage.</summary>
    public readonly record struct Ranked(string Id, AppEntry? App, LauncherCommand? Command, int Score, int Match)
    {
        /// <summary>Strong matches go above file results; scattered-letter matches go below them.</summary>
        public bool IsStrong => Match >= SearchKey.ScoreTokens - AliasPenalty;
    }

    /// <summary>A match on the executable or package name counts a little less than one on the real name.</summary>
    public const int AliasPenalty = 100;

    public static string Normalize(string query) => query.Trim().ToLowerInvariant();

    /// <summary>Apps and commands matching <paramref name="query"/>, best first (match quality + usage).</summary>
    public static List<Ranked> Rank(IEnumerable<AppEntry> apps, IEnumerable<LauncherCommand> commands, string query, LaunchStats stats, DateTime now)
    {
        string q = Normalize(query);
        var results = new List<Ranked>();
        if (q.Length == 0) return results;
        foreach (var app in apps)
        {
            int byAlias = app.Alias?.Match(q) ?? 0;
            int match = Math.Max(app.Key.Match(q), byAlias > 0 ? byAlias - AliasPenalty : 0);
            if (match > 0) results.Add(new Ranked(app.Id, app, null, match + stats.Boost(app.Id, q, now), match));
        }
        foreach (var cmd in commands)
        {
            // Commands sit just below apps with the same match quality.
            int match = cmd.Keys.Max(k => k.Match(q));
            if (match > 0) results.Add(new Ranked(cmd.Id, null, cmd, match - 5 + stats.Boost(cmd.Id, q, now), match - 5));
        }
        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.App?.Name.Length ?? r.Command!.Title.Length)
            .ToList();
    }

    private static readonly Regex UrlPattern = new(@"^(https?://)?([\w-]+\.)+(com|cn|net|org|io|dev|app|ai|co|me|gov|edu|info|xyz|top|tv|cc|us|uk|au|de|jp|hk|tw|sg|ca|fr|ru|gg|im|ly|to|site|tech|online|store)(:\d+)?(/\S*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"github.com/x" or "https://…" → a URL to open; anything else (file names like "report.docx") → null.</summary>
    public static string? AsUrl(string query)
    {
        string q = query.Trim();
        if (q.Contains(' ') || !UrlPattern.IsMatch(q)) return null;
        if (!q.Contains("://")) q = "https://" + q;
        return Uri.TryCreate(q, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }
}
