using System.IO;
using System.Text.Json;
using ChaoDesk.Host.Panel;
using ChaoDesk.Host.Settings;

namespace ChaoDesk.Host.Tests;

public class PanelLayoutTests
{
    [Fact]
    public void AddsMovesRemovesAndCapsAtFifteen()
    {
        int saves = 0;
        var layout = new PanelLayout([], _ => saves++);
        foreach (var action in PanelAction.All.Take(15)) Assert.True(layout.AddAction(action.Id));
        Assert.True(layout.IsFull); Assert.Equal(5, layout.Rows);
        Assert.False(layout.AddAction(PanelAction.All.Last().Id));
        string first = layout.Items[0].Id;
        Assert.True(layout.Move(first, 14)); Assert.Equal(first, layout.Items[14].Id);
        Assert.False(layout.Move(first, 15));
        Assert.True(layout.Remove(first)); Assert.Equal(14, layout.Items.Count);
        Assert.Equal(14, layout.HomeTileCount);
        Assert.True(layout.AddAction(PanelAction.All[0].Id));
        Assert.Equal(15, layout.HomeTileCount);
        Assert.Equal(18, saves);
    }

    [Fact]
    public void EmptyLayoutStaysEmptyAndUnknownOrDuplicateToolsAreRemoved()
    {
        Assert.Empty(PanelLayout.Normalize([]));
        Assert.Equal(5, PanelLayout.Normalize(null).Count);
        var items = PanelLayout.Normalize([
            new() { ActionId = "clipboard", Id = "same" },
            new() { ActionId = "clipboard", Id = "duplicate" },
            new() { ActionId = "unknown" },
            new() { Name = "Offline drive", Target = @"Z:\Offline\tool.exe", Id = "same" },
        ]);
        Assert.Equal(2, items.Count); Assert.NotEqual(items[0].Id, items[1].Id);
        Assert.Equal(@"Z:\Offline\tool.exe", items[1].Target); // temporarily unavailable targets survive loading
    }

    [Fact]
    public void HomeRowsContainOnlySavedActionsAndKeepOneEmptyRow()
    {
        (int Actions, int Tiles, int Rows, double Height)[] cases =
        [
            (0, 0, 1, 164), (2, 2, 1, 164), (3, 3, 1, 164),
            (5, 5, 2, 246), (6, 6, 2, 246), (14, 14, 5, 492), (15, 15, 5, 492)
        ];
        foreach (var item in cases)
        {
            var layout = new PanelLayout(PanelAction.All.Take(item.Actions).Select(action => new PanelItem { ActionId = action.Id }));
            Assert.Equal(item.Actions, layout.Items.Count);
            Assert.Equal(item.Tiles, layout.HomeTileCount);
            Assert.Equal(item.Rows, layout.Rows);
            Assert.Equal(item.Height, layout.HomeHeight);
        }
    }

    [Fact]
    public void CompactLabelsKeepFullNamesAndBothSearchVocabularies()
    {
        (string Id, string English, string Chinese)[] labels =
        [
            ("restore", "Reopen", "撤销关闭"), ("clipboard", "Clipboard", "剪贴板"),
            ("screenshot", "Screenshot", "截图"), ("ocr", "OCR", "识字"), ("pin", "Pin", "贴图")
        ];
        foreach (var label in labels)
        {
            var action = PanelAction.Find(label.Id)!;
            Assert.Equal(label.English, action.ShortTitle);
            Assert.Equal(label.Chinese, action.ShortChinese);
        }
        Assert.Equal("Recognise text", PanelAction.Find("ocr")!.Title);
        Assert.Equal("屏幕识字", PanelAction.Find("ocr")!.Chinese);
        Assert.Equal("Pin an area", PanelAction.Find("pin")!.Title);
        foreach (var action in PanelAction.All)
        {
            Assert.True(action.Matches(action.Title));
            Assert.True(action.Matches(action.Chinese));
            Assert.True(action.Matches(action.ShortTitle));
            Assert.True(action.Matches(action.ShortChinese));
        }
    }

    [Fact]
    public void CustomTargetsCanBeEditedAndRemovalNeverDeletesTheFile()
    {
        string file = Path.GetTempFileName();
        try
        {
            var layout = new PanelLayout([]);
            Assert.True(layout.SaveShortcut(null, "My file", file));
            string id = layout.Items[0].Id;
            Assert.True(layout.SaveShortcut(id, "Docs", "example.com"));
            Assert.Equal("https://example.com", layout.Items[0].Target);
            Assert.Equal(id, layout.Items[0].Id);
            Assert.False(layout.SaveShortcut(null, "Bad", "javascript:alert(1)"));
            Assert.True(layout.Remove(id)); Assert.True(File.Exists(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void SettingsRoundTripPreservesOrderEmptyLayoutAndWallpaperSelection()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PanelSettings-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(folder, "settings.json");
        try
        {
            var settings = new AppSettings();
            var layout = new PanelLayout(settings.PanelItems, items => settings.PanelItems = items);
            string first = layout.Items[0].Id;
            Assert.True(layout.Move(first, layout.Items.Count - 1));
            Assert.True(layout.SaveShortcut(null, "Docs", "https://example.com"));
            settings.WallpaperProperties["lotus-pond"] = new() { ["scene"] = "Wisteria garden" };
            settings.Save(file);
            var loaded = AppSettings.Load(file);
            Assert.Equal(layout.Items.Select(x => x.Id), loaded.PanelItems.Select(x => x.Id));
            Assert.Equal("https://example.com", loaded.PanelItems.Last().Target);
            Assert.Equal("Wisteria garden", loaded.WallpaperProperties["lotus-pond"]["scene"]!.GetValue<string>());
            loaded.PanelItems.Clear(); loaded.Save(file); Assert.Empty(AppSettings.Load(file).PanelItems);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
