using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace ChaoDesk.Host.Translate;

/// <summary>A translation and the language the text was found to be in.</summary>
public sealed record Translation(string Text, string Source, string Target, string Engine);

/// <summary>
/// Online translation without an account: Google's free endpoint (translate.googleapis.com, client=gtx) first,
/// then Microsoft's (Bing Translator's web page; cn.bing.com in mainland China, where Google is blocked); either can be
/// chosen alone. Text only leaves the PC when the user asks for a translation. Target "auto": Chinese text →
/// English, anything else → Chinese.
/// </summary>
public static class Translator
{
    public static readonly string[] Engines = ["auto", "google", "microsoft"];

    /// <summary>Target choices: "auto" then language codes (Google's; <see cref="MicrosoftCode"/> maps them).</summary>
    public static readonly string[] Targets = ["auto", "zh-CN", "en", "ja", "ko", "fr", "de", "es", "ru"];

    /// <summary>Each language in its own name, for the settings list.</summary>
    public static string NativeName(string code) => code switch
    {
        "zh-CN" => "简体中文", "en" => "English", "ja" => "日本語", "ko" => "한국어", "fr" => "Français",
        "de" => "Deutsch", "es" => "Español", "ru" => "Русский", _ => code,
    };

    private const int ChunkLimit = 4000; // characters per request; long text goes in several

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        return client;
    }

    /// <summary>Translates with the chosen engine ("auto" tries Google, then Microsoft). Throws when every engine failed.</summary>
    public static async Task<Translation> TranslateAsync(string text, string target, string engine, CancellationToken token = default)
    {
        string to = ResolveTarget(text, target);
        var order = engine switch { "google" => new[] { "google" }, "microsoft" => new[] { "microsoft" }, _ => new[] { "google", "microsoft" } };
        Exception? last = null;
        foreach (var e in order)
        {
            try
            {
                var chunks = Chunk(text, ChunkLimit);
                var parts = new List<string>(chunks.Count);
                string source = "";
                foreach (var chunk in chunks)
                {
                    var (translated, detected) = e == "google" ? await GoogleAsync(chunk, to, token) : await MicrosoftAsync(chunk, to, token);
                    parts.Add(translated);
                    if (source.Length == 0) source = detected;
                }
                return new Translation(string.Concat(parts), source, to, e);
            }
            catch (Exception ex) when (!token.IsCancellationRequested) // network, HTTP status or an answer in another shape
            {
                Log.Warning(ex, "Translation with {Engine} failed", e);
                last = ex;
            }
        }
        throw new HttpRequestException("No translation engine could be reached", last);
    }

    /// <summary>"auto": text that is mostly Han characters goes to English, anything else to Chinese.</summary>
    public static string ResolveTarget(string text, string target) =>
        target != "auto" && Targets.Contains(target) ? target : (IsMostlyChinese(text) ? "en" : "zh-CN");

    public static bool IsMostlyChinese(string text)
    {
        int han = 0, letters = 0;
        foreach (char c in text)
        {
            if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿') han++;
            else if (char.IsLetter(c)) letters++;
        }
        // Han characters carry a word each, latin letters about a fifth of one.
        return han > 0 && han * 5 >= letters;
    }

    /// <summary>Splits at line ends (else spaces) so no piece is longer than <paramref name="limit"/>; joined, they give the text back.</summary>
    public static List<string> Chunk(string text, int limit)
    {
        var chunks = new List<string>();
        int start = 0;
        while (text.Length - start > limit)
        {
            int end = text.LastIndexOf('\n', start + limit - 1, limit);
            if (end < start) end = text.LastIndexOf(' ', start + limit - 1, limit);
            end = end < start ? start + limit : end + 1;
            chunks.Add(text[start..end]);
            start = end;
        }
        chunks.Add(text[start..]);
        return chunks;
    }

    // ---- Google ---------------------------------------------------------------------------------------

    private static async Task<(string Text, string Source)> GoogleAsync(string text, string to, CancellationToken token)
    {
        // POST keeps long text out of the URL; the endpoint takes the same form fields as its query string.
        using var content = new FormUrlEncodedContent([new("q", text)]);
        string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={Uri.EscapeDataString(to)}&dt=t";
        using var response = await Http.PostAsync(url, content, token);
        response.EnsureSuccessStatusCode();
        return ParseGoogle(await response.Content.ReadAsStringAsync(token));
    }

    /// <summary>[[["译文","source",…],…],null,"en",…] → the joined translation and the detected language.</summary>
    public static (string Text, string Source) ParseGoogle(string json)
    {
        var root = JsonNode.Parse(json)!.AsArray();
        var sb = new StringBuilder();
        foreach (var segment in root[0]!.AsArray())
            if (segment?[0] is JsonValue v && v.TryGetValue<string>(out var s)) sb.Append(s);
        string source = root.Count > 2 && root[2] is JsonValue lang && lang.TryGetValue<string>(out var l) ? l : "";
        return (sb.ToString(), source);
    }

    // ---- Microsoft (Bing Translator's web page) -------------------------------------------------------

    /// <summary>Google's codes in Microsoft's spelling.</summary>
    public static string MicrosoftCode(string code) => code switch { "zh-CN" => "zh-Hans", _ => code };

    private const int BingChunkLimit = 1000; // the page's own limit per request

    /// <summary>The page's session: its cookies, host (cn.bing.com in mainland China), IG / IID and anti-abuse key + token.</summary>
    private sealed record BingSession(HttpClient Client, string Host, string Ig, string Iid, string Key, string Token, DateTime Expires);

    private static BingSession? _bing;

    private static async Task<(string Text, string Source)> MicrosoftAsync(string text, string to, CancellationToken token)
    {
        var sb = new StringBuilder();
        string source = "";
        foreach (var chunk in Chunk(text, BingChunkLimit))
        {
            var (t, s) = await BingAsync(chunk, to, token);
            sb.Append(t);
            if (source.Length == 0) source = s;
        }
        return (sb.ToString(), source);
    }

    private static async Task<(string Text, string Source)> BingAsync(string text, string to, CancellationToken token)
    {
        var session = _bing is { } b && DateTime.UtcNow < b.Expires ? b : _bing = await OpenBingAsync(token);
        string url = $"https://{session.Host}/ttranslatev3?isVertical=1&IG={session.Ig}&IID={session.Iid}.1";
        using var content = new FormUrlEncodedContent([
            new("fromLang", "auto-detect"), new("to", MicrosoftCode(to)), new("text", text), new("token", session.Token), new("key", session.Key)]);
        using var response = await session.Client.PostAsync(url, content, token);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(token);
        if (!json.TrimStart().StartsWith('['))
        {
            _bing = null; // an expired session answers {"statusCode":…}
            throw new InvalidOperationException("Bing Translator refused the request: " + json[..Math.Min(json.Length, 120)]);
        }
        return ParseMicrosoft(json);
    }

    private static async Task<BingSession> OpenBingAsync(CancellationToken token)
    {
        var handler = new HttpClientHandler { CookieContainer = new System.Net.CookieContainer(), UseCookies = true };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Http.DefaultRequestHeaders.UserAgent.ToString());
        using var response = await client.GetAsync("https://www.bing.com/translator", token);
        response.EnsureSuccessStatusCode();
        string page = await response.Content.ReadAsStringAsync(token);
        var (ig, iid, key, tok, ttl) = ParseBingPage(page);
        string host = response.RequestMessage?.RequestUri?.Host ?? "www.bing.com";
        return new BingSession(client, host, ig, iid, key, tok, DateTime.UtcNow.AddMilliseconds(Math.Min(ttl, 3_600_000) * 0.9));
    }

    /// <summary>IG:"…", data-iid="…" and params_AbusePreventionHelper = [key,"token",ttl] from the translator page.</summary>
    public static (string Ig, string Iid, string Key, string Token, long TtlMs) ParseBingPage(string page)
    {
        var ig = System.Text.RegularExpressions.Regex.Match(page, "IG:\"([0-9A-Fa-f]+)\"");
        var iid = System.Text.RegularExpressions.Regex.Match(page, "data-iid=\"([^\"]+)\"");
        var helper = System.Text.RegularExpressions.Regex.Match(page, "params_AbusePreventionHelper\\s*=\\s*\\[(\\d+),\"([^\"]+)\",(\\d+)");
        if (!ig.Success || !iid.Success || !helper.Success) throw new InvalidOperationException("The Bing Translator page has changed");
        return (ig.Groups[1].Value, iid.Groups[1].Value, helper.Groups[1].Value, helper.Groups[2].Value, long.Parse(helper.Groups[3].Value));
    }

    /// <summary>[{"detectedLanguage":{"language":"en"},"translations":[{"text":"…"}]}] → text and language.</summary>
    public static (string Text, string Source) ParseMicrosoft(string json)
    {
        var first = JsonNode.Parse(json)!.AsArray()[0]!;
        string text = first["translations"]![0]!["text"]!.GetValue<string>();
        string source = first["detectedLanguage"]?["language"]?.GetValue<string>() ?? "";
        return (text, source);
    }
}

/// <summary>Names of target languages and services in the interface language.</summary>
public static class TranslationNames
{
    /// <summary>A target or a detected source language ("zh-CN", Bing's "zh-Hans", "it"…) by name.</summary>
    public static string Language(string code)
    {
        string? english = code.Split('-')[0].ToLowerInvariant() switch
        {
            "zh" => "Chinese", "en" => "English", "ja" => "Japanese", "ko" => "Korean", "fr" => "French",
            "de" => "German", "es" => "Spanish", "ru" => "Russian", _ => null,
        };
        if (english != null) return Localization.Loc.T(english);
        // Others: Windows' own name for the language, in its own language (it has no Chinese names under an English Windows).
        try { return System.Globalization.CultureInfo.GetCultureInfo(code, predefinedOnly: true).NativeName; }
        catch (System.Globalization.CultureNotFoundException) { return code; }
    }

    public static string Engine(string engine) => engine == "microsoft" ? "Microsoft Bing" : "Google";
}
