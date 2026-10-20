using ChaoDesk.Host.Desktop;

namespace ChaoDesk.Host.Power;

[Flags]
public enum PauseReason
{
    None = 0,
    Fullscreen = 1 << 0,
    Maximized = 1 << 1,
    OnBattery = 1 << 2,
    BatterySaver = 1 << 3,
    SessionLocked = 1 << 4,
    DisplayOff = 1 << 5,
    Suspended = 1 << 6,
}

/// <summary>A top-level window relevant to pause decisions (already filtered to visible, uncloaked app windows).</summary>
public readonly record struct WindowSnapshot(PixelRect Bounds, bool IsMaximized, bool IsForeground, bool IsShell);

/// <summary>Everything the evaluator needs; gathered by <see cref="PausePolicy"/> from Win32.</summary>
public sealed record PauseInputs(
    IReadOnlyList<MonitorInfo> Monitors,
    IReadOnlyList<WindowSnapshot> Windows,
    bool ForegroundIsD3DFullscreen,
    bool OnBattery,
    bool BatterySaver,
    bool SessionLocked,
    bool DisplayOff,
    bool Suspended,
    bool PauseWhenMaximized,
    bool PauseOnBattery);

/// <summary>Pure pause rules. No Win32 calls, so it is unit-tested directly.</summary>
public static class PauseEvaluator
{
    /// <summary>A window counts as covering a monitor if it covers at least this fraction of the work area.</summary>
    public const double CoverFraction = 0.98;

    public static Dictionary<string, PauseReason> Evaluate(PauseInputs input)
    {
        var global = PauseReason.None;
        if (input.BatterySaver) global |= PauseReason.BatterySaver;
        if (input.OnBattery && input.PauseOnBattery) global |= PauseReason.OnBattery;
        if (input.SessionLocked) global |= PauseReason.SessionLocked;
        if (input.DisplayOff) global |= PauseReason.DisplayOff;
        if (input.Suspended) global |= PauseReason.Suspended;

        var result = new Dictionary<string, PauseReason>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in input.Monitors)
        {
            var r = global;
            foreach (var w in input.Windows)
            {
                if (w.IsShell) continue;
                if (w.IsForeground && IsFullscreenOn(w.Bounds, m, input.ForegroundIsD3DFullscreen))
                    r |= PauseReason.Fullscreen;
                if (input.PauseWhenMaximized && CoversDesktop(w, m))
                    r |= PauseReason.Maximized;
            }
            result[m.DeviceName] = r;
        }
        return result;
    }

    /// <summary>Fullscreen = the window covers the whole monitor (taskbar included).</summary>
    public static bool IsFullscreenOn(PixelRect window, MonitorInfo m, bool d3dFullscreen)
    {
        if (window.Covers(m.Bounds)) return true;
        // Exclusive-mode D3D apps may report odd rects; trust the shell's flag when the window is mostly on this monitor.
        return d3dFullscreen && window.Intersect(m.Bounds).Area * 2 > m.Bounds.Area;
    }

    /// <summary>Maximised on this monitor, or sized to (almost) fill its work area.</summary>
    public static bool CoversDesktop(WindowSnapshot w, MonitorInfo m)
    {
        var overlap = w.Bounds.Intersect(m.WorkArea);
        if (overlap.Area == 0 || m.WorkArea.Area == 0) return false;
        double fraction = (double)overlap.Area / m.WorkArea.Area;
        if (w.IsMaximized) return fraction > 0.5; // maximised onto this monitor
        return fraction >= CoverFraction;
    }
}
