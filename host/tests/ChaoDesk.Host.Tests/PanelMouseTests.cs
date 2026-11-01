using ChaoDesk.Host.Input;

namespace ChaoDesk.Host.Tests;

public class PanelMouseTests
{
    [Theory]
    [InlineData("middle", 0x207u, 0u, false, false)]
    [InlineData("middleDouble", 0x207u, 0u, false, false)]
    [InlineData("x1", 0x20Bu, 0x10000u, false, false)]
    [InlineData("x2", 0x20Bu, 0x20000u, false, false)]
    [InlineData("ctrlRight", 0x204u, 0u, true, false)]
    [InlineData("altLeft", 0x201u, 0u, false, true)]
    public void MatchesOnlyConfiguredMouseDown(string mode, uint message, uint data, bool ctrl, bool alt)
    {
        Assert.True(PanelMouseTrigger.Matches(mode, message, data, ctrl, alt, false, false));
        Assert.False(PanelMouseTrigger.Matches(mode, message + 1, data, ctrl, alt, false, false));
        Assert.False(PanelMouseTrigger.Matches(mode, message, data, ctrl, alt, true, false));
        Assert.False(PanelMouseTrigger.Matches(mode, message, data, ctrl, alt, false, true));
    }

    [Fact]
    public void LeavesUnselectedButtonsAndModifiedMiddleClicksAlone()
    {
        Assert.False(PanelMouseTrigger.Matches("none", 0x207, 0, false, false, false, false));
        Assert.False(PanelMouseTrigger.Matches("middle", 0x201, 0, false, false, false, false));
        Assert.False(PanelMouseTrigger.Matches("middle", 0x207, 0, true, false, false, false));
        Assert.False(PanelMouseTrigger.Matches("x1", 0x20B, 0x20000, false, false, false, false));
    }
}
