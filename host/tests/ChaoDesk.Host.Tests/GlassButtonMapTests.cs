using System.Windows;
using System.Windows.Media;
using ChaoDesk.Host.Panel;

namespace ChaoDesk.Host.Tests;

public class GlassButtonMapTests
{
    [Fact]
    public void ActionFieldPreservesIdentityDistanceAndBackground()
    {
        var map=GlassButtonMap.Create(128,96,[new Rect(10,10,40,40),new Rect(64,10,40,40)]);
        Assert.True(map.IsFrozen); Assert.Equal(PixelFormats.Bgr32,map.Format);
        byte[] pixels=new byte[128*96*4]; map.CopyPixels(pixels,128*4,0);
        byte Value(int x,int y,int channel)=>pixels[(y*128+x)*4+channel];
        Assert.Equal(16,Value(30,30,1)); Assert.True(Value(30,30,2)<192);
        Assert.Equal(32,Value(84,30,1)); Assert.True(Value(84,30,2)<192);
        Assert.True(Value(55,30,2)>=192); // Zero distance is 191.25 in the [-24,8] field.
        Assert.Equal(0,Value(126,94,1)); Assert.Equal(255,Value(126,94,2));
        Assert.Equal(0,Value(30,30,0)); // No contact shadow inside an action.
        Assert.True(Value(30,51,0)>0);
    }

    [Fact]
    public void EmptyAndClippedLayoutsDoNotLeaveStaleActions()
    {
        var map=GlassButtonMap.Create(40,30,[new Rect(-30,-20,40,40),null]);
        byte[] pixels=new byte[40*30*4]; map.CopyPixels(pixels,40*4,0);
        Assert.Equal(16,pixels[(5*40+2)*4+1]);
        map=GlassButtonMap.Create(40,30,[]); map.CopyPixels(pixels,40*4,0);
        for(int n=0;n<pixels.Length;n+=4) {
            Assert.Equal(0,pixels[n]); Assert.Equal(0,pixels[n+1]); Assert.Equal(255,pixels[n+2]);
        }
    }
}
