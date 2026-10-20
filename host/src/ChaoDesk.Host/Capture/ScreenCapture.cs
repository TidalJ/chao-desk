using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChaoDesk.Host.Desktop;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Capture;

/// <summary>Raw screen pixels: 32-bit BGRX, top-down, in physical pixels.</summary>
public sealed class ScreenPixels
{
    public required PixelRect Bounds { get; init; }
    public required byte[] Data { get; init; }
    public int Stride => Bounds.Width * 4;

    /// <summary>The pixel at screen coordinates (x, y), as 0xRRGGBB.</summary>
    public int ColorAt(int x, int y)
    {
        int px = Math.Clamp(x - Bounds.X, 0, Bounds.Width - 1), py = Math.Clamp(y - Bounds.Y, 0, Bounds.Height - 1);
        int i = py * Stride + px * 4;
        return (Data[i + 2] << 16) | (Data[i + 1] << 8) | Data[i];
    }

    private BitmapSource? _bitmap;

    /// <summary>The whole capture as a frozen bitmap (created once).</summary>
    public BitmapSource ToBitmap()
    {
        if (_bitmap != null) return _bitmap;
        var bmp = BitmapSource.Create(Bounds.Width, Bounds.Height, 96, 96, PixelFormats.Bgr32, null, Data, Stride);
        bmp.Freeze();
        return _bitmap = bmp;
    }

    /// <summary>A frozen bitmap of part of the capture (screen coordinates).</summary>
    public BitmapSource Crop(PixelRect r)
    {
        r = Intersect(r, Bounds);
        var cropped = new CroppedBitmap(ToBitmap(), new System.Windows.Int32Rect(r.X - Bounds.X, r.Y - Bounds.Y, r.Width, r.Height));
        cropped.Freeze();
        return cropped;
    }

    public static PixelRect Intersect(PixelRect a, PixelRect b)
    {
        int x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        int r = Math.Min(a.Right, b.Right), bottom = Math.Min(a.Bottom, b.Bottom);
        return r <= x || bottom <= y ? new PixelRect(x, y, 0, 0) : new PixelRect(x, y, r - x, bottom - y);
    }
}

/// <summary>A window (or a large child control) that a capture can snap to, in z-order.</summary>
public readonly record struct SnapRect(PixelRect Bounds, int Depth);

/// <summary>Screen grabbing (GDI BitBlt, physical pixels) and the window rectangles for snapping.</summary>
public static class ScreenCapture
{
    public static PixelRect VirtualScreen => new(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>Copies the pixels of a screen rectangle (layered windows included).</summary>
    public static ScreenPixels Grab(PixelRect r)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, r.Width, r.Height);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, SRCCOPY | CAPTUREBLT);
            SelectObject(mem, old);
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = r.Width,
                biHeight = -r.Height, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
            var data = new byte[r.Width * r.Height * 4];
            GetDIBits(mem, bmp, 0, (uint)r.Height, data, ref header, 0);
            return new ScreenPixels { Bounds = r, Data = data };
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// Visible top-level windows top to bottom, each followed by its larger child controls (deeper = more
    /// specific), so hovering can pick a window or, inside it, a panel. Our own windows are skipped.
    /// </summary>
    public static List<SnapRect> SnapTargets()
    {
        var result = new List<SnapRect>();
        uint ownPid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == ownPid) return true;
            var b = GetVisibleBounds(hwnd);
            if (b.Width < 8 || b.Height < 8) return true;
            result.Add(new SnapRect(PixelRect.From(b), 0));
            EnumChildWindows(hwnd, (child, _) =>
            {
                if (!IsWindowVisible(child)) return true;
                GetWindowRect(child, out RECT cr);
                if (cr.Width >= 60 && cr.Height >= 30 && (cr.Width < b.Width - 4 || cr.Height < b.Height - 4))
                    result.Add(new SnapRect(PixelRect.From(cr), 1));
                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>The most specific snap target under a point: the topmost window containing it, then its smallest child there.</summary>
    public static PixelRect? SnapAt(IReadOnlyList<SnapRect> targets, int x, int y)
    {
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i].Depth != 0 || !targets[i].Bounds.Contains(x, y)) continue;
            PixelRect best = targets[i].Bounds;
            for (int j = i + 1; j < targets.Count && targets[j].Depth > 0; j++)
            {
                var c = targets[j].Bounds;
                if (c.Contains(x, y) && (long)c.Width * c.Height < (long)best.Width * best.Height) best = c;
            }
            return best;
        }
        return null;
    }
}
