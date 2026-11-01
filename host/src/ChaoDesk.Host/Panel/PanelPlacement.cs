using ChaoDesk.Host.Desktop;

namespace ChaoDesk.Host.Panel;

internal readonly record struct PanelPlacementResult(MonitorInfo Monitor, PixelRect PixelBounds,
    double WidthDip, double HeightDip, double AvailableHeightDip, int MarginPixels);

/// <summary>Places each opening at the current pointer in physical screen coordinates.</summary>
internal static class PanelPlacement
{
    internal static PanelPlacementResult AtCursor(IReadOnlyList<MonitorInfo> monitors, int cursorX, int cursorY, double requestedHeightDip)
    {
        if (monitors.Count == 0) throw new ArgumentException("At least one display is required.", nameof(monitors));
        var monitor = monitors.FirstOrDefault(display => display.Bounds.Contains(cursorX, cursorY)) ??
            monitors.MinBy(display => DistanceSquared(display.Bounds, cursorX, cursorY))!;
        var work = monitor.WorkArea;
        if (work.Width <= 0 || work.Height <= 0) throw new ArgumentException("The display work area must have a positive size.", nameof(monitors));
        double scale = monitor.Scale > 0 ? monitor.Scale : 1;
        int margin = Math.Min((int)Math.Round(8 * scale), Math.Max(0, Math.Min(work.Width, work.Height) / 2 - 1));
        double availableWidth = (work.Width - 2 * margin) / scale;
        double availableHeight = (work.Height - 2 * margin) / scale;
        double widthDip = Math.Min(PanelLayout.Width, availableWidth);
        double heightDip = Math.Clamp(requestedHeightDip, 1 / scale, availableHeight);
        int width = Math.Clamp((int)Math.Round(widthDip * scale), 1, work.Width - 2 * margin);
        int height = Math.Clamp((int)Math.Round(heightDip * scale), 1, work.Height - 2 * margin);
        int x = Math.Clamp(cursorX - width / 2, work.X + margin, work.Right - margin - width);
        int y = Math.Clamp(cursorY - (int)Math.Round(80 * scale), work.Y + margin, work.Bottom - margin - height);
        return new(monitor, new PixelRect(x, y, width, height), widthDip, heightDip, availableHeight, margin);
    }

    private static double DistanceSquared(PixelRect bounds, int x, int y)
    {
        double dx = (double)x - Math.Clamp(x, bounds.X, bounds.Right - 1);
        double dy = (double)y - Math.Clamp(y, bounds.Y, bounds.Bottom - 1);
        return dx * dx + dy * dy;
    }
}
