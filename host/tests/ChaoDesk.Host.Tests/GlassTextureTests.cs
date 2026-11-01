using System.Buffers;
using System.Windows;
using System.Windows.Media.Imaging;
using ChaoDesk.Host.Panel;

namespace ChaoDesk.Host.Tests;

public class GlassTextureTests
{
    [Fact]
    public void RebaseKeepsThePreviouslyDisplayedPixelsAndCoordinatesTogether()
    {
        var texture=new GlassTexture();
        using var first=Frame(-160,20,12);
        Assert.True(texture.TryUpload(first));
        var previous=texture.Bitmap!;
        using var rebased=Frame(-95,20,230); // Same size, shifted by the 65px handoff.
        Assert.True(texture.TryUpload(rebased));
        Assert.NotSame(previous,texture.Bitmap);
        Assert.Equal((byte)12,FirstPixel(previous));
        Assert.Equal((byte)230,FirstPixel(texture.Bitmap!));
        Assert.Equal(new Rect(-95,20,8,8),texture.Bounds);
        using var vertical=Frame(-95,85,64);
        previous=texture.Bitmap!;
        Assert.True(texture.TryUpload(vertical));
        Assert.NotSame(previous,texture.Bitmap); Assert.Equal((byte)230,FirstPixel(previous));
    }

    [Fact]
    public void ContentChangesWithinTheSameRegionReuseTheTexture()
    {
        var texture=new GlassTexture();
        using var first=Frame(0,0,12); Assert.True(texture.TryUpload(first));
        var original=texture.Bitmap;
        using var changed=Frame(0,0,100); Assert.True(texture.TryUpload(changed));
        Assert.Same(original,texture.Bitmap); Assert.Equal((byte)100,FirstPixel(texture.Bitmap!));
    }

    private static GlassBackdrop.Frame Frame(int x,int y,byte gray)
    {
        var pixels=ArrayPool<byte>.Shared.Rent(8*8*4);
        Array.Fill(pixels,gray,0,8*8*4);
        return new GlassBackdrop.Frame(pixels,8,8,x,y);
    }
    private static byte FirstPixel(BitmapSource bitmap)
    {
        byte[] pixel=new byte[4]; bitmap.CopyPixels(new Int32Rect(0,0,1,1),pixel,4,0); return pixel[0];
    }
}
