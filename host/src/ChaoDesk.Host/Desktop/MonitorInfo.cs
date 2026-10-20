using System.Runtime.InteropServices;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Desktop;

/// <summary>A physical display. All rectangles are in physical pixels (the process is Per-Monitor V2 aware).</summary>
public sealed record MonitorInfo(
    IntPtr Handle,
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    uint Dpi,
    bool IsPrimary)
{
    /// <summary>Scale factor relative to 96 DPI (1.0 = 100 %, 1.5 = 150 %).</summary>
    public double Scale => Dpi / 96.0;

    public override string ToString() =>
        $"{DeviceName} {Bounds} @ {Dpi} dpi ({Scale:P0}){(IsPrimary ? " primary" : "")}";

    public static IReadOnlyList<MonitorInfo> GetAll()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(h, ref mi)) return true;
            uint dpi = GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 ? dx : 96;
            list.Add(new MonitorInfo(h, mi.szDevice ?? "", PixelRect.From(mi.rcMonitor), PixelRect.From(mi.rcWork),
                dpi, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        // Stable order: primary first, then left-to-right, top-to-bottom.
        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
    }
}

/// <summary>Integer rectangle in physical pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Contains(int px, int py) => px >= X && py >= Y && px < Right && py < Bottom;

    /// <summary>True if this rectangle fully covers <paramref name="other"/>.</summary>
    public bool Covers(PixelRect other) =>
        X <= other.X && Y <= other.Y && Right >= other.Right && Bottom >= other.Bottom;

    public PixelRect Intersect(PixelRect o)
    {
        int x1 = Math.Max(X, o.X), y1 = Math.Max(Y, o.Y);
        int x2 = Math.Min(Right, o.Right), y2 = Math.Min(Bottom, o.Bottom);
        return x2 > x1 && y2 > y1 ? new PixelRect(x1, y1, x2 - x1, y2 - y1) : default;
    }

    public long Area => (long)Width * Height;

    public override string ToString() => $"({X},{Y}) {Width}x{Height}";

    internal static PixelRect From(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
