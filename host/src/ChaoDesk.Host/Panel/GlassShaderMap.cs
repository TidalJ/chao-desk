using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChaoDesk.Host.Panel;

// Port of rdev/liquid-glass-react src/shader-utils.ts (MIT). See Maps/LICENSE.txt.
internal static class GlassShaderMap
{
    internal static BitmapSource Create(int width, int height)
    {
        var offsets = new (double X, double Y)[width*height];
        double maximum = 0;
        for (int y=0;y<height;y++) for (int x=0;x<width;x++) {
            double ix=(double)x/width-.5, iy=(double)y/height-.5;
            double qx=Math.Abs(ix)-.3+.6, qy=Math.Abs(iy)-.2+.6;
            double d=Math.Min(Math.Max(qx,qy),0)+Math.Sqrt(Math.Pow(Math.Max(qx,0),2)+Math.Pow(Math.Max(qy,0),2))-.6;
            double t=Math.Clamp((d-.15-.8)/-.8,0,1); t=t*t*(3-2*t); t=t*t*(3-2*t);
            double dx=ix*(t-1)*width, dy=iy*(t-1)*height;
            maximum=Math.Max(maximum,Math.Max(Math.Abs(dx),Math.Abs(dy)));
            offsets[y*width+x]=(dx,dy);
        }
        maximum=Math.Max(1,maximum);
        byte[] pixels=new byte[width*height*4];
        for(int y=0;y<height;y++) for(int x=0;x<width;x++) {
            var (dx,dy)=offsets[y*width+x];
            double edge=Math.Min(1,Math.Min(Math.Min(x,y),Math.Min(width-x-1,height-y-1))/2.0);
            int at=(y*width+x)*4;
            // Canvas ImageData uses Uint8ClampedArray: round to nearest, ties to
            // even. Truncation biased neutral 127.5 down and shifted every ray.
            pixels[at]=Channel(dy*edge/maximum);
            pixels[at+1]=pixels[at];
            pixels[at+2]=Channel(dx*edge/maximum);
            pixels[at+3]=255;
        }
        var result=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);
        result.Freeze(); return result;
    }

    private static byte Channel(double displacement) =>
        (byte)Math.Clamp(Math.Round((displacement+.5)*255,MidpointRounding.ToEven),0,255);
}
