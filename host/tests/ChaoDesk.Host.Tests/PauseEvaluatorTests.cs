using ChaoDesk.Host.Desktop;
using ChaoDesk.Host.Power;

namespace ChaoDesk.Host.Tests;

public class PauseEvaluatorTests
{
    // Primary 1920x1080 with a 48 px taskbar, secondary 2560x1440 to the left (negative X).
    private static readonly MonitorInfo Primary = new(IntPtr.Zero, "DISPLAY1",
        new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1032), 120, true);
    private static readonly MonitorInfo Secondary = new(IntPtr.Zero, "DISPLAY2",
        new PixelRect(-2560, -200, 2560, 1440), new PixelRect(-2560, -200, 2560, 1392), 96, false);

    private static PauseInputs Inputs(IReadOnlyList<WindowSnapshot> windows,
        bool battery = false, bool saver = false, bool locked = false, bool pauseMax = true, bool pauseBattery = true,
        bool d3d = false) =>
        new([Primary, Secondary], windows, d3d, battery, saver, locked, false, false, pauseMax, pauseBattery);

    [Fact]
    public void NoWindows_NothingPaused()
    {
        var r = PauseEvaluator.Evaluate(Inputs([]));
        Assert.Equal(PauseReason.None, r["DISPLAY1"]);
        Assert.Equal(PauseReason.None, r["DISPLAY2"]);
    }

    [Fact]
    public void ForegroundFullscreen_PausesOnlyItsMonitor()
    {
        var fs = new WindowSnapshot(new PixelRect(0, 0, 1920, 1080), false, IsForeground: true, IsShell: false);
        var r = PauseEvaluator.Evaluate(Inputs([fs]));
        Assert.True(r["DISPLAY1"].HasFlag(PauseReason.Fullscreen));
        Assert.Equal(PauseReason.None, r["DISPLAY2"]);
    }

    [Fact]
    public void FullscreenOnNegativeCoordinateMonitor()
    {
        var fs = new WindowSnapshot(new PixelRect(-2560, -200, 2560, 1440), false, true, false);
        var r = PauseEvaluator.Evaluate(Inputs([fs]));
        Assert.True(r["DISPLAY2"].HasFlag(PauseReason.Fullscreen));
        Assert.False(r["DISPLAY1"].HasFlag(PauseReason.Fullscreen));
    }

    [Fact]
    public void MaximizedWindow_PausesWhenEnabled()
    {
        // Maximised windows overhang the work area by the resize border.
        var max = new WindowSnapshot(new PixelRect(-8, -8, 1936, 1048), IsMaximized: true, IsForeground: false, IsShell: false);
        Assert.True(PauseEvaluator.Evaluate(Inputs([max]))["DISPLAY1"].HasFlag(PauseReason.Maximized));
        Assert.Equal(PauseReason.None, PauseEvaluator.Evaluate(Inputs([max], pauseMax: false))["DISPLAY1"]);
    }

    [Fact]
    public void SmallWindow_DoesNotPause()
    {
        var w = new WindowSnapshot(new PixelRect(100, 100, 800, 600), false, true, false);
        Assert.Equal(PauseReason.None, PauseEvaluator.Evaluate(Inputs([w]))["DISPLAY1"]);
    }

    [Fact]
    public void ShellWindows_AreIgnored()
    {
        var desktop = new WindowSnapshot(new PixelRect(-2560, -200, 4480, 1440), false, true, IsShell: true);
        var r = PauseEvaluator.Evaluate(Inputs([desktop]));
        Assert.Equal(PauseReason.None, r["DISPLAY1"]);
        Assert.Equal(PauseReason.None, r["DISPLAY2"]);
    }

    [Fact]
    public void Battery_RespectsSetting_BatterySaverAlwaysPauses()
    {
        Assert.True(PauseEvaluator.Evaluate(Inputs([], battery: true))["DISPLAY1"].HasFlag(PauseReason.OnBattery));
        Assert.Equal(PauseReason.None, PauseEvaluator.Evaluate(Inputs([], battery: true, pauseBattery: false))["DISPLAY1"]);
        Assert.True(PauseEvaluator.Evaluate(Inputs([], saver: true, pauseBattery: false))["DISPLAY2"].HasFlag(PauseReason.BatterySaver));
    }

    [Fact]
    public void Locked_PausesEverything()
    {
        var r = PauseEvaluator.Evaluate(Inputs([], locked: true));
        Assert.True(r["DISPLAY1"].HasFlag(PauseReason.SessionLocked));
        Assert.True(r["DISPLAY2"].HasFlag(PauseReason.SessionLocked));
    }

    [Fact]
    public void D3DFullscreenFlag_CountsWhenMostlyOnMonitor()
    {
        var odd = new WindowSnapshot(new PixelRect(0, 0, 1600, 900), false, true, false);
        Assert.True(PauseEvaluator.Evaluate(Inputs([odd], d3d: true))["DISPLAY1"].HasFlag(PauseReason.Fullscreen));
        Assert.False(PauseEvaluator.Evaluate(Inputs([odd], d3d: false))["DISPLAY1"].HasFlag(PauseReason.Fullscreen));
    }
}
