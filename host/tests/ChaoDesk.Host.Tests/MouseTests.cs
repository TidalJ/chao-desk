using ChaoDesk.Host.Desktop;
using ChaoDesk.Host.Input;

namespace ChaoDesk.Host.Tests;

public class ClickSynthesizerTests
{
    private readonly ClickSynthesizer _c = new(5, 300);

    [Fact]
    public void QuickStillPress_IsClick()
    {
        _c.OnDown(100, 100, 1000, PointerButton.Left);
        _c.OnMove(103, 98);
        Assert.True(_c.OnUp(102, 101, 1200, PointerButton.Left));
    }

    [Fact]
    public void Drag_IsNotClick_EvenIfReleasedAtStart()
    {
        _c.OnDown(100, 100, 1000, PointerButton.Left);
        _c.OnMove(160, 140); // rubber-band selection
        _c.OnMove(100, 100);
        Assert.False(_c.OnUp(100, 100, 1100, PointerButton.Left));
    }

    [Fact]
    public void SlowPress_IsNotClick()
    {
        _c.OnDown(10, 10, 1000, PointerButton.Left);
        Assert.False(_c.OnUp(10, 10, 1301, PointerButton.Left));
    }

    [Fact]
    public void DifferentButton_IsNotClick()
    {
        _c.OnDown(10, 10, 1000, PointerButton.Left);
        Assert.False(_c.OnUp(10, 10, 1050, PointerButton.Right));
    }

    [Fact]
    public void TickWraparound_IsHandled()
    {
        _c.OnDown(10, 10, uint.MaxValue - 50, PointerButton.Left);
        Assert.True(_c.OnUp(10, 10, 100, PointerButton.Left));
    }
}

public class PointerMappingTests
{
    [Fact]
    public void MapsPhysicalToCssPixels_WithScaleAndNegativeOrigin()
    {
        var m = new MonitorInfo(IntPtr.Zero, "D2", new PixelRect(-2880, -120, 2880, 1620), new PixelRect(-2880, -120, 2880, 1572), 144, false);
        string json = AppHost.PointerJson(new DesktopPointerEvent(PointerEventKind.Click, -1440, 690, PointerButton.Left), m);
        Assert.Equal("""{"type":"pointer","event":"click","x":960,"y":540,"button":"left"}""", json);
    }

    [Fact]
    public void UsesInvariantDecimalSeparator()
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var m = new MonitorInfo(IntPtr.Zero, "D1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1032), 120, true);
            string json = AppHost.PointerJson(new DesktopPointerEvent(PointerEventKind.Move, 101, 1, PointerButton.None), m);
            Assert.Equal("""{"type":"pointer","event":"move","x":80.8,"y":0.8,"button":"none"}""", json);
        }
        finally { Thread.CurrentThread.CurrentCulture = prev; }
    }
}
