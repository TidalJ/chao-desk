using System.Text.Json;
using System.Windows;
using ChaoDesk.Host.Panel;
using ChaoDesk.Host.Settings;

namespace ChaoDesk.Host.Tests;
public class GlassOptionsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(2)]
    public void SlowMotionReusesRegionWithoutConsumingOpticalGuard(double dpi)
    {
        var desktop = new Rect(-1920, -400, 5760, 2560);
        var window = new Rect(-1000, 100, Math.Ceiling(324*dpi), Math.Ceiling(440*dpi));
        int margin = (int)Math.Ceiling(96 + 60*dpi);
        var first = GlassCaptureRegion.Select(window, desktop, margin, Rect.Empty);
        var region = first;
        for (int pixel = 1; pixel <= 200; pixel++)
        {
            window.Offset(1, 0);
            var selected = GlassCaptureRegion.Select(window, desktop, margin, region);
            var optical = window; optical.Inflate(margin-96, margin-96);
            Assert.True(selected.Contains(optical));
            if (pixel <= 64) Assert.Equal(first, selected);
            if (pixel == 65) Assert.NotEqual(first, selected);
            region = selected;
        }
    }

    [Fact]
    public void CaptureRegionHandlesEdgesResizeAndDisplayChanges()
    {
        var desktop = new Rect(-1920, 0, 3840, 1080);
        var window = new Rect(-1910, 12, 405, 550);
        var first = GlassCaptureRegion.Select(window, desktop, 180, Rect.Empty);
        Assert.True(desktop.Contains(first));
        window.Offset(40, 20);
        Assert.Equal(first, GlassCaptureRegion.Select(window, desktop, 180, first));
        window.Height += 60;
        var resized = GlassCaptureRegion.Select(window, desktop, 180, first);
        Assert.Equal(first.Height+60, resized.Height);
        var singleDisplay = new Rect(0, 0, 1920, 1080);
        window.X = 500;
        Assert.True(singleDisplay.Contains(GlassCaptureRegion.Select(window, singleDisplay, 180, resized)));
        Assert.Equal(window, GlassCaptureRegion.Select(window, singleDisplay, 0, resized));
    }

    [Fact]
    public void LegacySettingsGetReferenceDefaultsAndOptionsRoundTrip()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{}")!.Normalize();
        Assert.Equal(70,old.PanelGlass.Displacement); Assert.Equal(140,old.PanelGlass.Saturation);
        var adjusted = old.PanelGlass with { Mode="polar", Displacement=135, Blur=.25, Aberration=4, Radius=32 };
        old.PanelGlass=adjusted;
        Assert.Equal(adjusted,JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(old))!.Normalize().PanelGlass);
        old.PanelGlass=new() { Displacement=double.NaN, Blur=5, Saturation=-10, Mode="bad" };
        old.Normalize();
        Assert.Equal(70,old.PanelGlass.Displacement); Assert.Equal(1,old.PanelGlass.Blur);
        Assert.Equal(100,old.PanelGlass.Saturation); Assert.Equal("standard",old.PanelGlass.Mode);
    }

    [Theory]
    [InlineData(-1500,-100,1)]
    [InlineData(100,200,1.25)]
    [InlineData(500,300,2)]
    public void OverscanRemainsScreenAnchoredWhileWindowMoves(double x,double y,double dpi)
    {
        var captured=new Rect(x-160,y-160,324*dpi+320,440*dpi+320);
        foreach(int delta in new[] { -100,0,30,140 }) {
            var window=new Rect(x+delta,y+delta/2.0,324*dpi,440*dpi);
            var map=GlassCaptureMapping.Create(captured,window);
            Assert.Equal(window.Left,captured.Left+map.X*captured.Width,8);
            Assert.Equal(window.Top,captured.Top+map.Y*captured.Height,8);
            Assert.Equal(window.Right,captured.Left+(map.X+map.Z)*captured.Width,8);
            Assert.Equal(window.Bottom,captured.Top+(map.Y+map.W)*captured.Height,8);
        }
    }
}
