using System.Windows;
using System.Windows.Media.Media3D;

namespace ChaoDesk.Host.Panel;
internal static class GlassCaptureMapping
{
    // Physical screen rectangles, independent of monitor DPI and negative desktop origins.
    internal static Point4D Create(Rect captured, Rect window) => new(
        (window.X-captured.X)/captured.Width, (window.Y-captured.Y)/captured.Height,
        window.Width/captured.Width, window.Height/captured.Height);
}
