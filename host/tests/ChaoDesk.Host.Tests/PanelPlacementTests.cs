using ChaoDesk.Host.Desktop;
using ChaoDesk.Host.Panel;

namespace ChaoDesk.Host.Tests;

public sealed class PanelPlacementTests
{
    private static MonitorInfo Display(int x, int y, int width, int height, uint dpi = 96) =>
        new(IntPtr.Zero, "Owned display", new(x, y, width, height), new(x, y, width, height - 40), dpi, x == 0 && y == 0);

    [Fact]
    public void EachOpeningUsesTheCurrentCursorWithoutRememberingDraggedCoordinates()
    {
        var display = Display(0, 0, 1920, 1080);
        var first = PanelPlacement.AtCursor([display], 400, 300, 246);
        var second = PanelPlacement.AtCursor([display], 1200, 700, 246);
        Assert.Equal(new PixelRect(238, 220, 324, 246), first.PixelBounds);
        Assert.Equal(new PixelRect(1038, 620, 324, 246), second.PixelBounds);
    }

    [Fact]
    public void CursorSelectsTheNegativeCoordinateMonitorAndItsOwnDpi()
    {
        var primary = Display(0, 0, 1920, 1080);
        var left = Display(-2560, -160, 2560, 1440, 144);
        var placement = PanelPlacement.AtCursor([primary, left], -1200, 500, 246);
        Assert.Same(left, placement.Monitor);
        Assert.Equal(new PixelRect(-1443, 380, 486, 369), placement.PixelBounds);
        Assert.Equal(324, placement.WidthDip);
        Assert.Equal(246, placement.HeightDip);
    }

    [Theory]
    [InlineData(0, 0, 8, 8)]
    [InlineData(1919, 0, 1588, 8)]
    [InlineData(0, 1079, 8, 786)]
    [InlineData(1919, 1079, 1588, 786)]
    public void WorkAreaEdgesKeepTheWholePanelAboveTheTaskbar(int x, int y, int expectedX, int expectedY)
    {
        var display = Display(0, 0, 1920, 1080);
        var placement = PanelPlacement.AtCursor([display], x, y, 246);
        Assert.Equal(expectedX, placement.PixelBounds.X);
        Assert.Equal(expectedY, placement.PixelBounds.Y);
        Assert.True(display.WorkArea.Covers(placement.PixelBounds));
    }

    [Theory]
    [InlineData(120, 405, 308, 10)]
    [InlineData(144, 486, 369, 12)]
    [InlineData(192, 648, 492, 16)]
    public void ScaleAppliesToTheSizeCursorOffsetAndWorkAreaMargin(uint dpi, int width, int height, int margin)
    {
        var display = Display(0, 0, 2560, 1440, dpi);
        var placement = PanelPlacement.AtCursor([display], 1000, 600, 246);
        Assert.Equal(width, placement.PixelBounds.Width);
        Assert.Equal(height, placement.PixelBounds.Height);
        Assert.Equal(600 - (int)Math.Round(80 * display.Scale), placement.PixelBounds.Y);
        Assert.Equal(margin, placement.MarginPixels);
    }

    [Fact]
    public void SmallWorkAreasConstrainDimensionsInsteadOfPlacingThePanelOffscreen()
    {
        var display = Display(-400, 0, 400, 360, 192);
        var placement = PanelPlacement.AtCursor([display], -2, 350, 492);
        Assert.Equal(184, placement.WidthDip);
        Assert.Equal(144, placement.HeightDip);
        Assert.Equal(new PixelRect(-384, 16, 368, 288), placement.PixelBounds);
        Assert.True(display.WorkArea.Covers(placement.PixelBounds));
    }

    [Fact]
    public void APointBetweenDisplaysUsesTheNearestDisplayInsteadOfThePrimary()
    {
        var primary = Display(0, 0, 1920, 1080);
        var left = Display(-2560, -200, 1920, 1080, 144);
        var placement = PanelPlacement.AtCursor([primary, left], -600, 300, 246);
        Assert.Same(left, placement.Monitor);
        Assert.True(left.WorkArea.Covers(placement.PixelBounds));
    }
}
