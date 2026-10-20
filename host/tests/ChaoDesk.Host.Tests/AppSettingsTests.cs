using System.IO;
using System.Text.Json.Nodes;
using ChaoDesk.Host.Settings;

namespace ChaoDesk.Host.Tests;

public class AppSettingsTests
{
    [Fact]
    public void RoundTrips()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lp-settings-{Guid.NewGuid():N}.json");
        try
        {
            var s = new AppSettings { FpsLimit = 60, RenderScale = 0.75, Quality = QualityMode.Low, PauseOnBattery = false };
            s.PropertiesFor("lotus-pond")["fishCount"] = JsonValue.Create(20);
            s.Save(path);

            var loaded = AppSettings.Load(path);
            Assert.Equal(60, loaded.FpsLimit);
            Assert.Equal(0.75, loaded.RenderScale);
            Assert.Equal(QualityMode.Low, loaded.Quality);
            Assert.False(loaded.PauseOnBattery);
            Assert.Equal(20, loaded.PropertiesFor("lotus-pond")["fishCount"]!.GetValue<int>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void InvalidValues_AreNormalized()
    {
        var s = new AppSettings { FpsLimit = 144, RenderScale = 3 }.Normalize();
        Assert.Equal(30, s.FpsLimit);
        Assert.Equal(1.0, s.RenderScale);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lp-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            var s = AppSettings.Load(path);
            Assert.Equal(30, s.FpsLimit);
            Assert.True(s.MouseInteraction);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bad");
        }
    }

    [Fact]
    public void DefaultHotkeysAreAllDifferentAndUsable()
    {
        var s = new AppSettings().Normalize();
        string[] keys = [s.LauncherHotkey, s.ClipboardHotkey, s.ScreenshotHotkey, s.QuickScreenshotHotkey, s.ScrollScreenshotHotkey,
                         s.PinHotkey, s.PinsToggleHotkey, s.OcrHotkey];
        Assert.Equal(["Alt+Space", "Shift+`", "Shift+F2", "Shift+F1", "Shift+F4", "Shift+F3", "Ctrl+F3", "Ctrl+`"], keys);
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.All(keys, k => Assert.True(Input.Hotkey.Parse(k).IsUsable, k));
    }

    [Fact]
    public void SavedHotkeysAreKept()
    {
        var s = new AppSettings { ScreenshotHotkey = "F1", QuickScreenshotHotkey = "Ctrl+F1", PinHotkey = "F3", PinsToggleHotkey = "Shift+F3", OcrHotkey = "Shift+F6" }.Normalize();
        Assert.Equal(("F1", "Ctrl+F1", "F3", "Shift+F3", "Shift+F6"), (s.ScreenshotHotkey, s.QuickScreenshotHotkey, s.PinHotkey, s.PinsToggleHotkey, s.OcrHotkey));
    }
}
