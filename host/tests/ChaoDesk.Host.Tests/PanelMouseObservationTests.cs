using ChaoDesk.Host.Input;

namespace ChaoDesk.Host.Tests;

public class PanelMouseObservationTests
{
    [Theory]
    [InlineData(0x201u)]
    [InlineData(0x204u)]
    [InlineData(0x207u)]
    [InlineData(0x20Bu)]
    public void OrdinaryButtonDownCanDismissWithoutActivation(uint message)
    {
        Assert.True(PanelMouseHook.IsObservablePress(message,0));
        Assert.False(PanelMouseHook.IsObservablePress(message+1,0)); // Opening gesture's release cannot dismiss.
        Assert.False(PanelMouseHook.IsObservablePress(message,1));
        Assert.False(PanelMouseHook.IsObservablePress(message,3)); // Injected input is ignored.
    }

    [Fact]
    public void MotionAndWheelDoNotDismiss()
    {
        Assert.False(PanelMouseHook.IsObservablePress(0x200,0));
        Assert.False(PanelMouseHook.IsObservablePress(0x20A,0));
        Assert.False(PanelMouseHook.IsObservablePress(0x20E,0));
    }
}
