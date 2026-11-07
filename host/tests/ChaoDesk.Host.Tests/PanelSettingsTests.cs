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
    public void ThePanelLookFollowsTheThemeAndCanBeAdjusted()
    {
        var celadon = Settings.ThemeManager.FindPalette("celadon");
        Assert.Equal("theme", Settings.ThemeManager.Resolve(celadon, Settings.ThemeManager.Adjust.None, false).Style.Panel);
        Assert.Equal("liquid", Settings.ThemeManager.Resolve(Settings.ThemeManager.FindPalette("aurora"), Settings.ThemeManager.Adjust.None, false).Style.Panel);
        Assert.Equal("smoked", Settings.ThemeManager.Resolve(celadon, new Settings.ThemeManager.Adjust(Panel: "smoked"), false).Style.Panel);
        Assert.Equal("theme", Settings.ThemeManager.Resolve(celadon, new Settings.ThemeManager.Adjust(Panel: "frosted"), false).Style.Panel);
    }

    [Fact]
    public void PanelHotkeyHasDefaultAndCanBeDisabled()
    {
        Assert.Equal("Ctrl+Space", new AppSettings().Normalize().PanelHotkey);
        Assert.Equal("", new AppSettings { PanelHotkey = "" }.Normalize().PanelHotkey);
        Assert.Equal("Ctrl+Space", new AppSettings { PanelHotkey = "invalid+invalid" }.Normalize().PanelHotkey);
        Assert.Equal("Ctrl+Alt+P", new AppSettings { PanelHotkey = "Ctrl+Alt+P" }.Normalize().PanelHotkey);
        Assert.Equal("middle", new AppSettings { PanelMouseTrigger = "unknown" }.Normalize().PanelMouseTrigger);
        Assert.Equal("none", new AppSettings { PanelMouseTrigger = "none" }.Normalize().PanelMouseTrigger);
    }
}
