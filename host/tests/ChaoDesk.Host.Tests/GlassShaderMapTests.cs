using System.Windows.Media;
using ChaoDesk.Host.Panel;

namespace ChaoDesk.Host.Tests;

public class GlassShaderMapTests
{
    [Fact]
    public void NeutralPixelsMatchCanvasTiesToEvenQuantization()
    {
        var map=GlassShaderMap.Create(24,18);
        Assert.True(map.IsFrozen);
        Assert.Equal(PixelFormats.Bgra32,map.Format);
        byte[] pixels=new byte[24*18*4]; map.CopyPixels(pixels,24*4,0);
        for(int y=0;y<18;y++) for(int x=0;x<24;x++)
        {
            if(x!=0 && x!=23 && y!=0 && y!=17 && (x!=12 || y!=9)) continue;
            int at=(y*24+x)*4;
            // Zero displacement maps to 127.5, rounded to 128 by ImageData.
            Assert.Equal((byte)128,pixels[at]);
            Assert.Equal((byte)128,pixels[at+1]);
            Assert.Equal((byte)128,pixels[at+2]);
            Assert.Equal((byte)255,pixels[at+3]);
        }
    }

    [Fact]
    public void DisplacementMatchesPinnedReferenceImageData()
    {
        // Golden RGB values from upstream shader-utils.ts at commit
        // ac48eab18d1f7f444ae30002d240cae29c863a21, using Uint8ClampedArray.
        var map=GlassShaderMap.Create(24,18);
        byte[] pixels=new byte[24*18*4]; map.CopyPixels(pixels,24*4,0);
        (int X,int Y,byte R,byte B)[] samples=
        [
            (1,1,210,187), (2,2,217,190), (5,4,133,131),
            (19,13,126,127), (21,15,89,102), (22,16,83,96)
        ];
        foreach(var sample in samples)
        {
            int at=(sample.Y*24+sample.X)*4;
            Assert.Equal(sample.B,pixels[at]);
            Assert.Equal(sample.B,pixels[at+1]);
            Assert.Equal(sample.R,pixels[at+2]);
            Assert.Equal((byte)255,pixels[at+3]);
        }
    }
}
