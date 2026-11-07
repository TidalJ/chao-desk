using ChaoDesk.Host.Settings;

namespace ChaoDesk.Host.Tests;

public class AccentTests
{
    [Theory]
    [InlineData("#2E6B5E", true)]
    [InlineData("2e6b5e", true)]
    [InlineData(" #1F6FB2 ", true)]
    [InlineData("#12345", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParsesSixDigitHexColours(string? text, bool valid) =>
        Assert.Equal(valid, ThemeManager.TryParseAccent(text, out _));

    [Fact]
    public void PresetsAndThemesAreValid()
    {
        Assert.All(ThemeManager.Accents, a => Assert.True(ThemeManager.TryParseAccent(a.Hex, out _)));
        Assert.All(ThemeManager.GradientAccents, a => Assert.True(ThemeManager.TryParseAccent(a.Start, out _) && ThemeManager.TryParseAccent(a.End, out _)));
        Assert.Equal("celadon", ThemeManager.Palettes[0].Id);
        Assert.Equal(ThemeManager.Palettes.Length, ThemeManager.Palettes.Select(p => p.Id).Distinct().Count());
        Assert.True(ThemeManager.Palettes.Count(p => p.Tags.Contains("gradient")) >= 7);
    }

    [Fact]
    public void AnUnreadableAccentIsDropped()
    {
        var s = new AppSettings { AccentColor = "blue" }.Normalize();
        Assert.Equal("", s.AccentColor);
        Assert.Empty(s.UserThemes);
        Assert.Equal("celadon", s.Palette);
    }

    [Fact]
    public void OlderColoursBecomeAThemeOfOnesOwn()
    {
        var s = new AppSettings { Palette = "lotus", AccentColor = "#1F6FB2" }.Normalize();
        var theme = Assert.Single(s.UserThemes);
        Assert.Equal(theme.Id, s.Palette);
        Assert.Equal("lotus", theme.Base);
        Assert.Equal("#1F6FB2", theme.Adjust.Accent);
        Assert.Equal("", s.AccentColor);

        var dark = new AppSettings { BackgroundColor = "#1E2124" }.Normalize();
        Assert.Equal("#1E2124", Assert.Single(dark.UserThemes).Adjust.Dark!.Colours[0]);
        Assert.Null(dark.UserThemes[0].Adjust.Light);

        var older = new AppSettings { Palette = "custom", CustomTheme = new("#EEF2F7", "#4655C8", 0.5, "hero-summer.jpg") }.Normalize();
        var own = Assert.Single(older.UserThemes);
        Assert.Equal(own.Id, older.Palette);
        Assert.Equal("hero-summer.jpg", own.Adjust.Hero);
        Assert.Equal("#EEF2F7", own.Adjust.Light!.Colours[0]);
        Assert.Null(older.CustomTheme);
    }

    [Fact]
    public void AnUnknownThemeFallsBackToCeladon() =>
        Assert.Equal("celadon", new AppSettings { Palette = "no-such-theme" }.Normalize().Palette);

    [Fact]
    public void AdjustmentsChangeOnlyWhatTheyName()
    {
        var aurora = ThemeManager.FindPalette("aurora");
        var plain = ThemeManager.Resolve(aurora, ThemeManager.Adjust.None, false);
        var adjusted = ThemeManager.Resolve(aurora, new ThemeManager.Adjust(Radius: 4, Accent: "#1F6FB2", AccentEnd: ""), false);
        Assert.Equal(4, adjusted.Style.Radius);
        Assert.Equal(plain.Style.Card, adjusted.Style.Card);
        Assert.Equal(plain.Style.Sidebar, adjusted.Style.Sidebar);
        Assert.Equal("#1F6FB2", ThemeManager.Hex(adjusted.Shades.Accent));
        Assert.Null(adjusted.Version.AccentEnd);
        Assert.NotNull(plain.Version.AccentEnd);
        Assert.Equal(["Accent", "Corners"], new ThemeManager.Adjust(Radius: 4, Accent: "#1F6FB2").Changes().ToArray());
    }

    [Fact]
    public void ADarkPageOfOnesOwnTurnsTheLightVersionDark()
    {
        var look = ThemeManager.Resolve(ThemeManager.FindPalette("celadon"), new ThemeManager.Adjust(Light: new ThemeManager.Backdrop("solid", ["#12202B"])), dark: false);
        Assert.True(look.Dark);
        Assert.Equal("#12202B", ThemeManager.Hex(look.Shades.Page));
        Assert.True(ThemeManager.Lightness(look.Shades.Ink) > 0.7);
    }
}
