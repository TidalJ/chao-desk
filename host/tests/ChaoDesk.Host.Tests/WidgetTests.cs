using ChaoDesk.Host.Settings;

namespace ChaoDesk.Host.Tests;

public class WidgetTests
{
    [Fact]
    public void NormalizeKeepsOrderDropsUnknownAndAppendsMissing()
    {
        var list = WidgetCatalog.Normalize([
            new WidgetItem { Id = "weather", Enabled = true },
            new WidgetItem { Id = "nope", Enabled = true },
            new WidgetItem { Id = "calendar", Enabled = false },
            new WidgetItem { Id = "weather", Enabled = false },
        ]);
        Assert.Equal("weather", list[0].Id);
        Assert.True(list[0].Enabled);
        Assert.Equal("calendar", list[1].Id);
        Assert.False(list[1].Enabled);
        Assert.Equal(WidgetCatalog.Catalog.Length, list.Count);
        Assert.DoesNotContain(list, w => w.Id == "nope");
        Assert.False(list.Single(w => w.Id == "clock").Enabled); // new widgets come with their default
        Assert.True(list.Single(w => w.Id == "rate").Enabled);
    }

    [Fact]
    public void ShortcutTargetsMustBeWebAddressesOrExistingPaths()
    {
        Assert.True(WidgetCatalog.IsValidTarget("https://www.boc.cn/sourcedb/whpj/"));
        Assert.True(WidgetCatalog.IsValidTarget(Environment.SystemDirectory));
        Assert.False(WidgetCatalog.IsValidTarget("file:///C:/Windows"));
        Assert.False(WidgetCatalog.IsValidTarget(@"C:\definitely\not\here.exe"));
        Assert.False(WidgetCatalog.IsValidTarget("relative\\path"));
    }
}
