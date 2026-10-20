using System.Windows.Input;
using ChaoDesk.Host.Input;
using ChaoDesk.Host.Launcher;

namespace ChaoDesk.Host.Tests;

public class SearchKeyTests
{
    [Theory]
    [InlineData("微信", "wx")]
    [InlineData("微信", "weixin")]
    [InlineData("微信", "weix")]
    [InlineData("微信", "wxin")]
    [InlineData("微信", "微x")]
    [InlineData("微信", "微信")]
    [InlineData("网易云音乐", "wyy")]
    [InlineData("网易云音乐", "yyy")]
    [InlineData("网易云音乐", "yinyue")]
    [InlineData("Visual Studio Code", "vsc")]
    [InlineData("Visual Studio Code", "vscode")]
    [InlineData("Visual Studio Code", "code")]
    [InlineData("Visual Studio Code", "studio")]
    [InlineData("7-Zip File Manager", "7z")]
    [InlineData("PowerShell", "ps")]
    [InlineData("QQ音乐", "qqyy")]
    [InlineData("长江", "cj")] // polyphonic 长 (chang/zhang)
    [InlineData("长江", "zj")]
    public void Matches(string name, string query) => Assert.True(new SearchKey(name).Match(query) > 0, $"{query} → {name}");

    [Theory]
    [InlineData("微信", "wxy")]
    [InlineData("微信", "xw")]
    [InlineData("Visual Studio Code", "vz")]
    [InlineData("Notepad", "")]
    public void DoesNotMatch(string name, string query) => Assert.Equal(0, new SearchKey(name).Match(query));

    [Fact]
    public void BetterMatchesScoreHigher()
    {
        Assert.Equal(SearchKey.ScoreExact, new SearchKey("Word").Match("word"));
        Assert.Equal(SearchKey.ScorePrefix, new SearchKey("WordPad").Match("word"));
        Assert.Equal(SearchKey.ScoreWordSubstring, new SearchKey("Microsoft Word").Match("word"));
        Assert.Equal(SearchKey.ScoreTokensFromStart, new SearchKey("微信").Match("wx"));
        Assert.Equal(SearchKey.ScoreTokens, new SearchKey("企业微信").Match("wx"));
        Assert.Equal(SearchKey.ScoreSubsequence, new SearchKey("Notepad").Match("ntp"));
    }
}

public class CalculatorTests
{
    [Theory]
    [InlineData("1+2", 3)]
    [InlineData("12*(3+4)", 84)]
    [InlineData("2^10", 1024)]
    [InlineData("2^3^2", 512)]
    [InlineData("-3+5", 2)]
    [InlineData("10/4", 2.5)]
    [InlineData("10 % 4", 2)]
    [InlineData("3×4÷2", 6)]
    [InlineData("1.5*2", 3)]
    public void Evaluates(string text, double expected) => Assert.Equal(expected, Calculator.TryEvaluate(text)!.Value, 9);

    [Theory]
    [InlineData("42")]
    [InlineData("hello")]
    [InlineData("1+")]
    [InlineData("(1+2")]
    [InlineData("1/0")]
    [InlineData("2024-")]
    [InlineData("-5")]
    public void RejectsNonExpressions(string text) => Assert.Null(Calculator.TryEvaluate(text));

    [Fact]
    public void FormatsWithoutNoise()
    {
        Assert.Equal("0.3", Calculator.Format(0.1 + 0.2));
        Assert.Equal("1024", Calculator.Format(1024));
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Alt+Space", ModifierKeys.Alt, Key.Space)]
    [InlineData("alt + space", ModifierKeys.Alt, Key.Space)]
    [InlineData("Shift+`", ModifierKeys.Shift, Key.OemTilde)]
    [InlineData("Shift+~", ModifierKeys.Shift, Key.OemTilde)]
    [InlineData("Ctrl+Shift+V", ModifierKeys.Control | ModifierKeys.Shift, Key.V)]
    [InlineData("Win+1", ModifierKeys.Windows, Key.D1)]
    [InlineData("F9", ModifierKeys.None, Key.F9)]
    public void Parses(string text, ModifierKeys mods, Key key) => Assert.Equal(new Hotkey(mods, key), Hotkey.Parse(text));

    [Theory]
    [InlineData("Alt+Space")]
    [InlineData("Shift+`")]
    [InlineData("Ctrl+Alt+K")]
    [InlineData("Win+1")]
    public void RoundTrips(string text) => Assert.Equal(text, Hotkey.Parse(text).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("A+B")]
    [InlineData("Ctrl+Nope")]
    public void RejectsGarbage(string text) => Assert.True(Hotkey.Parse(text).IsEmpty || !Hotkey.Parse(text).IsUsable);

    [Fact]
    public void PlainLetterIsNotUsable() => Assert.False(Hotkey.Parse("K").IsUsable);

    [Fact]
    public void KnowsWhatATypingHotkeySwallows()
    {
        Assert.Equal("~", Hotkey.Parse("Shift+`").TypedText());
        Assert.Equal("`", new Hotkey(ModifierKeys.None, Key.OemTilde).TypedText());
        Assert.Null(Hotkey.Parse("Alt+Space").TypedText());
    }
}

public class LauncherSearchTests
{
    private static AppEntry App(string name) => new() { Name = name, ParsingName = name, Key = new SearchKey(name) };

    [Fact]
    public void LearnedQueryWins()
    {
        var apps = new[] { App("WPS Office"), App("微信") };
        var stats = new LaunchStats();
        var now = new DateTime(2026, 10, 17);
        Assert.Equal("WPS Office", LauncherSearch.Rank(apps, [], "w", stats, now)[0].App!.Name);
        stats.Record("app:微信", "w", now);
        Assert.Equal("微信", LauncherSearch.Rank(apps, [], "w", stats, now)[0].App!.Name);
    }

    [Fact]
    public void ForgettingRemovesUseAndLearnedQueriesButKeepsPins()
    {
        var apps = new[] { App("WPS Office"), App("微信") };
        var stats = new LaunchStats();
        var now = new DateTime(2026, 10, 17);
        stats.Record("app:微信", "w", now);
        stats.Record("app:WPS Office", "wp", now);
        stats.TogglePin(new LauncherItem { Id = "app:WPS Office", Title = "WPS Office", Kind = LauncherItemKind.App });
        stats.Forget("app:微信");
        Assert.DoesNotContain("app:微信", stats.Frequent(10));
        Assert.Equal("WPS Office", LauncherSearch.Rank(apps, [], "w", stats, now)[0].App!.Name);
        stats.ClearHistory();
        Assert.Empty(stats.Frequent(10));
        Assert.Single(stats.Pins);
    }

    [Fact]
    public void PinnedAppsRiseAndUnpinningUndoesIt()
    {
        var apps = new[] { App("WPS Office"), App("WinRAR") };
        var stats = new LaunchStats();
        var now = new DateTime(2026, 10, 17);
        Assert.Equal("WinRAR", LauncherSearch.Rank(apps, [], "w", stats, now)[0].App!.Name); // shorter name first on a tie
        var wps = new LauncherItem { Id = "app:WPS Office", Title = "WPS Office", Kind = LauncherItemKind.App };
        Assert.True(stats.TogglePin(wps));
        Assert.Equal("WPS Office", LauncherSearch.Rank(apps, [], "w", stats, now)[0].App!.Name);
        Assert.False(stats.TogglePin(wps));
        Assert.Empty(stats.Pins);
    }

    [Fact]
    public void CommandsMatchByKeyword()
    {
        var cmd = new LauncherCommand { Title = "Exit Chao Desk", Keywords = ["quit"], Run = () => { } };
        Assert.Single(LauncherSearch.Rank([], [cmd], "quit", new LaunchStats(), DateTime.Now));
    }

    [Theory]
    [InlineData("github.com", "https://github.com/")]
    [InlineData("https://example.org/a?b=1", "https://example.org/a?b=1")]
    [InlineData("baidu.com/s", "https://baidu.com/s")]
    public void RecognisesUrls(string query, string url) => Assert.Equal(url, LauncherSearch.AsUrl(query));

    [Theory]
    [InlineData("report.docx")]
    [InlineData("hello world.com")]
    [InlineData("notes")]
    [InlineData("setup.exe")]
    public void IgnoresNonUrls(string query) => Assert.Null(LauncherSearch.AsUrl(query));

    [Fact]
    public void ClutterIsSkipped()
    {
        Assert.True(AppCatalog.IsClutter("Uninstall Foo", null));
        Assert.True(AppCatalog.IsClutter("卸载 Foo", null));
        Assert.True(AppCatalog.IsClutter("Foo", @"C:\Foo\unins000.exe"));
        Assert.True(AppCatalog.IsClutter("Read me", @"C:\Foo\readme.txt"));
        Assert.False(AppCatalog.IsClutter("Foo", @"C:\Foo\foo.exe"));
    }
}
