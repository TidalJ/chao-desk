using ChaoDesk.Host.Settings;
using ChaoDesk.Host.Panel;

namespace ChaoDesk.Host.Tests;

public class PanelSettingsTests
{
    [Fact]
    public void NewSettingsUseFiveActionsWithoutReplacingExistingLayouts()
    {
        Assert.Equal(new[] { "restore", "clipboard", "screenshot", "ocr", "pin" },
            new AppSettings().Normalize().PanelItems.Select(item => item.ActionId));

        // A saved layout from the former twelve-action default is user data,
        // just like a reordered or custom layout; new defaults must not migrate it.
        var existing = PanelAction.All.Take(12).Reverse()
            .Select(action => new PanelItem { Id = "saved-" + action.Id, ActionId = action.Id }).ToList();
        var settings = new AppSettings { PanelItems = existing }.Normalize();
        Assert.Equal(existing.Select(item => item.Id), settings.PanelItems.Select(item => item.Id));
        Assert.Equal(existing.Select(item => item.ActionId), settings.PanelItems.Select(item => item.ActionId));
        Assert.Empty(new AppSettings { PanelItems = [] }.Normalize().PanelItems);
    }

    [Fact]
    public void PanelHotkeyHasDefaultAndCanBeDisabled()
    {
        Assert.Equal("Ctrl+Space", new AppSettings().Normalize().PanelHotkey);
        Assert.Equal("", new AppSettings { PanelHotkey = "" }.Normalize().PanelHotkey);
        Assert.Equal("Ctrl+Space", new AppSettings { PanelHotkey = "invalid+invalid" }.Normalize().PanelHotkey);
        Assert.Equal("Ctrl+Alt+P", new AppSettings { PanelHotkey = "Ctrl+Alt+P" }.Normalize().PanelHotkey);
        Assert.Equal("middle", new AppSettings { PanelMouseTrigger = "unknown" }.Normalize().PanelMouseTrigger);
        Assert.Equal("liquid", new AppSettings { PanelMaterial = "unknown" }.Normalize().PanelMaterial);
        Assert.Equal("liquid", new AppSettings { PanelMaterial = "solid" }.Normalize().PanelMaterial);
        Assert.Equal("liquid", new AppSettings { PanelMaterial = "frosted" }.Normalize().PanelMaterial);
        Assert.Equal("smoked", new AppSettings { PanelMaterial = "smoked" }.Normalize().PanelMaterial);
        Assert.Equal("none", new AppSettings { PanelMouseTrigger = "none" }.Normalize().PanelMouseTrigger);
    }
}
