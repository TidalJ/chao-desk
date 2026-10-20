using ChaoDesk.Host.Translate;

namespace ChaoDesk.Host.Tests;

public class TranslatorTests
{
    [Theory]
    [InlineData("你好，世界", true)]
    [InlineData("Hello world", false)]
    [InlineData("用 Python 写一个 script", true)]           // a few latin words in Chinese text
    [InlineData("The 中 character means middle", false)]    // one Han character in English text
    [InlineData("12345", false)]
    public void DetectsChinese(string text, bool chinese) => Assert.Equal(chinese, Translator.IsMostlyChinese(text));

    [Theory]
    [InlineData("你好", "auto", "en")]
    [InlineData("Hello", "auto", "zh-CN")]
    [InlineData("Bonjour", "auto", "zh-CN")]
    [InlineData("Hello", "ja", "ja")]
    [InlineData("Hello", "xx", "zh-CN")] // unknown target: automatic
    public void ResolvesTargets(string text, string target, string expected) => Assert.Equal(expected, Translator.ResolveTarget(text, target));

    [Fact]
    public void ChunksAtLineEndsAndKeepsEveryCharacter()
    {
        string text = string.Join("\n", Enumerable.Range(0, 300).Select(i => $"Line {i} with some words in it"));
        var chunks = Translator.Chunk(text, 1000);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 1000));
        Assert.All(chunks.SkipLast(1), c => Assert.EndsWith("\n", c));
        Assert.Equal(text, string.Concat(chunks));
    }

    [Fact]
    public void ChunksTextWithoutBreaks()
    {
        string text = new('x', 2500);
        var chunks = Translator.Chunk(text, 1000);
        Assert.Equal([1000, 1000, 500], chunks.Select(c => c.Length));
    }

    [Fact]
    public void ParsesGoogle()
    {
        const string json = """[[["你好世界。","Hello world. ",null,null,10],["你今天怎么样？","How are you today?",null,null,10]],null,"en",null,null,null,1,[]]""";
        var (text, source) = Translator.ParseGoogle(json);
        Assert.Equal("你好世界。你今天怎么样？", text);
        Assert.Equal("en", source);
    }

    [Fact]
    public void ParsesMicrosoft()
    {
        const string json = """[{"translations":[{"text":"你好，世界。","to":"zh-Hans"}],"detectedLanguage":{"language":"en","score":1}}]""";
        var (text, source) = Translator.ParseMicrosoft(json);
        Assert.Equal("你好，世界。", text);
        Assert.Equal("en", source);
    }

    [Fact]
    public void ReadsTheBingPage()
    {
        const string page = """
            <script>_G={IG:"C3E8DA84A1E344E6"};</script><div id="rich_tta" data-iid="translator.5023"></div>
            <script>var params_AbusePreventionHelper = [1792501302078,"q8Fx7JWj2ZnwjPod",3600000];</script>
            """;
        var (ig, iid, key, token, ttl) = Translator.ParseBingPage(page);
        Assert.Equal(("C3E8DA84A1E344E6", "translator.5023", "1792501302078", "q8Fx7JWj2ZnwjPod", 3600000L), (ig, iid, key, token, ttl));
        Assert.Throws<InvalidOperationException>(() => Translator.ParseBingPage("<html>changed</html>"));
    }

    /// <summary>Calls the real services; runs only with CHAODESK_LIVE=1 (they are online and unofficial).</summary>
    [Theory]
    [InlineData("google")]
    [InlineData("microsoft")]
    public async Task TranslatesOnline(string engine)
    {
        if (Environment.GetEnvironmentVariable("CHAODESK_LIVE") != "1") return;
        var t = await Translator.TranslateAsync("Good morning. The weather is nice today.", "auto", engine);
        Assert.Equal(engine, t.Engine);
        Assert.Equal("zh-CN", t.Target);
        Assert.True(Translator.IsMostlyChinese(t.Text), t.Text);
        var back = await Translator.TranslateAsync("今天天气很好。", "auto", engine);
        Assert.Equal("en", back.Target);
        Assert.Contains("weather", back.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("zh-CN", "Chinese")]
    [InlineData("zh-Hans", "Chinese")]
    [InlineData("en", "English")]
    [InlineData("it", "italiano")]
    [InlineData("qq-nope", "qq-nope")]
    public void NamesLanguages(string code, string name) => Assert.Equal(name, TranslationNames.Language(code));

    [Fact]
    public void MapsCodesForMicrosoft()
    {
        Assert.Equal("zh-Hans", Translator.MicrosoftCode("zh-CN"));
        Assert.Equal("ja", Translator.MicrosoftCode("ja"));
    }
}
