using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChaoDesk.Host.Panel;

/// <summary>Layout-only data: R = distance [-24,8] DIP, G = action id, B = contact shadow.</summary>
internal static class GlassButtonMap
{
    internal static BitmapSource Create(int width, int height, IReadOnlyList<Rect?> slots)
    {
        width = Math.Clamp(width, 1, 1024); height = Math.Clamp(height, 1, 2048);
        byte[] pixels = new byte[width * height * 4];
        float[] nearest = new float[width * height];
        Array.Fill(nearest, 32);
        for (int i=0;i<nearest.Length;i++) { pixels[i*4+2]=255; pixels[i*4+3]=255; }
        for (int slot=0;slot<Math.Min(15,slots.Count);slot++)
        {
            if (slots[slot] is not { Width: > 0, Height: > 0 } rect) continue;
            double radius=Math.Min(12,Math.Min(rect.Width,rect.Height)/2);
            int left=Math.Max(0,(int)Math.Floor(rect.Left-20)), top=Math.Max(0,(int)Math.Floor(rect.Top-20));
            int right=Math.Min(width,(int)Math.Ceiling(rect.Right+20)), bottom=Math.Min(height,(int)Math.Ceiling(rect.Bottom+20));
            for(int y=top;y<bottom;y++) for(int x=left;x<right;x++)
            {
                double px=x+.5-rect.X-rect.Width/2, py=y+.5-rect.Y-rect.Height/2;
                double d=Distance(px,py,rect.Width/2,rect.Height/2,radius);
                int pixel=y*width+x, at=pixel*4;
                if(d<nearest[pixel]) {
                    nearest[pixel]=(float)d;
                    // Spend the 8-bit distance range on the visible bevel. A
                    // +/-32 DIP field quantized each step to .25 DIP and made
                    // subpixel corner motion jump even with shader coverage AA.
                    // More range inside preserves the fading reflection until
                    // its strongest possible contribution is below 1/255.
                    pixels[at+2]=(byte)Math.Clamp((int)Math.Round((d+24)*255/32),0,255);
                    pixels[at+1]=(byte)((slot+1)*16);
                }
                double shifted=Distance(px-1.5,py-2,rect.Width/2,rect.Height/2,radius);
                double edge=Math.Clamp((d+.5)/1.5,0,1);
                byte shadow=(byte)Math.Round(255*Math.Exp(-Math.Max(0,shifted)/3)*edge*edge*(3-2*edge));
                pixels[at]=Math.Max(pixels[at],shadow);
            }
        }
        // Bgr32 avoids alpha premultiplication changing the encoded data channels.
        var bitmap=BitmapSource.Create(width,height,96,96,PixelFormats.Bgr32,null,pixels,width*4);
        bitmap.Freeze(); return bitmap;
    }

    private static double Distance(double x,double y,double halfWidth,double halfHeight,double radius)
    {
        double qx=Math.Abs(x)-halfWidth+radius, qy=Math.Abs(y)-halfHeight+radius;
        double ox=Math.Max(0,qx), oy=Math.Max(0,qy);
        return Math.Sqrt(ox*ox+oy*oy)+Math.Min(Math.Max(qx,qy),0)-radius;
    }
}
