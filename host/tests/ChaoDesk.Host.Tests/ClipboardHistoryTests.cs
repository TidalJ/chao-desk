using System.IO;
using ChaoDesk.Host.Clips;

namespace ChaoDesk.Host.Tests;

public class ClipboardHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ChaoDeskClipTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private static ClipItem Text(string s) => new() { Kind = ClipKind.Text, Text = s, Hash = ClipItem.HashOf(ClipKind.Text, s) };

    [Fact]
    public void NewestFirstAndDuplicatesMoveUp()
    {
        var h = new ClipboardHistory(_dir, 10);
        h.Add(Text("a"));
        h.Add(Text("b"));
        h.Add(Text("a"));
        Assert.Equal(["a", "b"], h.Items.Select(i => i.Text));
    }

    [Fact]
    public void TrimsOldestButKeepsPinned()
    {
        var h = new ClipboardHistory(_dir, 3);
        var first = h.Add(Text("pinned"));
        h.TogglePin(first.Id);
        for (int i = 0; i < 5; i++) h.Add(Text($"t{i}"));
        Assert.Equal(["t4", "t3", "t2", "pinned"], h.Items.Select(i => i.Text));
    }

    [Fact]
    public void SavesImagesAndReloads()
    {
        var h = new ClipboardHistory(_dir, 10);
        h.Add(Text("hello"));
        var img = h.Add(new ClipItem { Kind = ClipKind.Image, ImageWidth = 1, ImageHeight = 1, Hash = "img1" }, [1, 2, 3]);
        h.Save();
        Assert.True(File.Exists(h.ImagePath(img)));

        var loaded = new ClipboardHistory(_dir, 10);
        loaded.Load();
        Assert.Equal(2, loaded.Items.Count);
        Assert.Equal(ClipKind.Image, loaded.Items[0].Kind);

        loaded.Remove(loaded.Items[0].Id);
        Assert.False(File.Exists(h.ImagePath(img)));
    }

    [Fact]
    public void SearchesTextAndSourceWithFilters()
    {
        var h = new ClipboardHistory(_dir, 10);
        var a = Text("Meeting at 3pm");
        a.Source = "微信";
        h.Add(a);
        h.Add(new ClipItem { Kind = ClipKind.Files, Text = @"C:\x\report.pdf", Hash = "f" });
        Assert.Single(h.Search("meeting", ClipFilter.All));
        Assert.Single(h.Search("微信", ClipFilter.All));
        Assert.Single(h.Search("report", ClipFilter.Files));
        Assert.Empty(h.Search("report", ClipFilter.Text));
        Assert.Empty(h.Search("", ClipFilter.Pinned));
    }

    [Fact]
    public void ClearKeepsPinned()
    {
        var h = new ClipboardHistory(_dir, 10);
        var keep = h.Add(Text("keep"));
        h.TogglePin(keep.Id);
        h.Add(Text("drop"));
        h.ClearUnpinned();
        Assert.Equal(["keep"], h.Items.Select(i => i.Text));
    }
}
