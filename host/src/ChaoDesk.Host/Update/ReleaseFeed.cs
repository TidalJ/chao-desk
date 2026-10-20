using System.IO;
using System.Text.Json;

namespace ChaoDesk.Host.Update;

/// <summary>The newest published release: its version, notes and installer.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string Notes,
    string PageUrl,
    string InstallerName,
    string InstallerUrl,
    long InstallerSize,
    string Sha256);

/// <summary>
/// Reads latest.json, the small manifest every release carries (written by release.yml). It is fetched from
/// github.com/&lt;repo&gt;/releases/latest/download/latest.json, a plain download: unlike the REST API (60 requests an
/// hour per IP without a login, often shared behind NAT) it has no rate limit. Pure, so it is tested offline.
/// </summary>
public static class ReleaseFeed
{
    /// <summary>"v0.3.0", "0.3" or "1.2.3-beta" → 0.3.0 / 0.3.0 / 1.2.3 (always three parts); null if it is no version.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        if (!s.Contains('.')) s += ".0";
        if (!Version.TryParse(s, out var v)) return null;
        return Normalize(v);
    }

    /// <summary>Major.Minor.Build, missing parts as 0 (assembly versions carry a fourth part we never use).</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>
    /// The release described by a latest.json, or null when anything needed is missing: a version, an https
    /// installer URL and its SHA-256 (an installer that cannot be checked is never offered).
    /// </summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var version = ParseVersion(Str(root, "version"));
        string? name = Str(root, "installer"), url = Str(root, "url"), sha = Str(root, "sha256");
        if (version == null || string.IsNullOrEmpty(name) || url == null || sha == null) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!IsSha256(sha) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return new ReleaseInfo(
            version,
            (Str(root, "notes") ?? "").Replace("\r\n", "\n").Trim(),
            Str(root, "page") ?? "",
            name,
            url,
            root.TryGetProperty("size", out var size) && size.TryGetInt64(out long n) ? n : 0,
            sha.ToLowerInvariant());
    }

    private static bool IsSha256(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
