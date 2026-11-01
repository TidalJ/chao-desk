using System.Windows;

namespace ChaoDesk.Host.Panel;

/// <summary>Physical screen pixels. Keep the texture origin stable while its optical guard still covers the pane.</summary>
internal static class GlassCaptureRegion
{
    public static Rect Select(Rect window, Rect desktop, int margin, Rect previous)
    {
        if (margin <= 0) return window;
        double width = Math.Min(desktop.Width, window.Width + margin * 2);
        double height = Math.Min(desktop.Height, window.Height + margin * 2);
        // The caller reserves 96 px beyond the optical footprint. Spend at most
        // 64 px on movement, retaining 32 px for the asynchronous capture handoff.
        var required = window;
        required.Inflate(Math.Max(0, margin - 64), Math.Max(0, margin - 64));
        required.Intersect(desktop);
        if (!previous.IsEmpty && previous.Width == width && previous.Height == height &&
            desktop.Contains(previous) && previous.Contains(required)) return previous;
        return new Rect(Math.Clamp(window.Left - margin, desktop.Left, desktop.Right - width),
            Math.Clamp(window.Top - margin, desktop.Top, desktop.Bottom - height), width, height);
    }
}
