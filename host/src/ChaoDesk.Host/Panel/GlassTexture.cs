using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChaoDesk.Host.Panel;

/// <summary>A displayed texture never changes its screen-space identity.</summary>
internal sealed class GlassTexture
{
    internal WriteableBitmap? Bitmap { get; private set; }
    internal Rect Bounds { get; private set; } = Rect.Empty;

    internal bool TryUpload(GlassBackdrop.Frame frame)
    {
        var bounds=new Rect(frame.X,frame.Y,frame.Width,frame.Height);
        // WriteableBitmap pixel copies and visual-property updates travel through
        // different rendering work. Never overwrite a visible texture with pixels
        // from a different origin, even if its dimensions are unchanged. A new
        // image resource and its mapping can then be submitted in the same batch.
        var target=Bitmap != null && Bounds == bounds ? Bitmap
            : new WriteableBitmap(frame.Width,frame.Height,96,96,PixelFormats.Bgr32,null);
        if (!target.TryLock(new Duration(TimeSpan.Zero))) return false;
        try
        {
            int stride=frame.Width*4;
            if (target.BackBufferStride == stride)
                Marshal.Copy(frame.Pixels,0,target.BackBuffer,stride*frame.Height);
            else
                for(int row=0;row<frame.Height;row++)
                    Marshal.Copy(frame.Pixels,row*stride,target.BackBuffer+row*target.BackBufferStride,stride);
            target.AddDirtyRect(new Int32Rect(0,0,frame.Width,frame.Height));
        }
        finally { target.Unlock(); }
        Bitmap=target; Bounds=bounds;
        return true;
    }
}
