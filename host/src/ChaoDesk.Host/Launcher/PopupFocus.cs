using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Launcher;

/// <summary>
/// The popups close when the user goes elsewhere. But Word's and PowerPoint's previews are windows of another process
/// inside the popup: clicking one (to select text, or right-clicking for its menu) moves the focus there, which Windows
/// reports as leaving. These helpers tell that apart from really leaving.
/// </summary>
internal static class PopupFocus
{
    /// <summary>
    /// True when the foreground window is the popup itself, or belongs to another process that has a window inside
    /// the popup (a preview handler, or the menu it opened).
    /// </summary>
    public static bool StillInside(Window window)
    {
        IntPtr own = new WindowInteropHelper(window).Handle;
        IntPtr fg = GetForegroundWindow();
        if (own == IntPtr.Zero || fg == IntPtr.Zero || fg == own || GetAncestor(fg, GA_ROOT) == own) return true;
        GetWindowThreadProcessId(fg, out uint fgPid);
        if (fgPid == (uint)Environment.ProcessId) return false; // another Chao Desk window (settings, Quick Look)
        bool inside = false;
        EnumChildWindows(own, (child, _) =>
        {
            GetWindowThreadProcessId(child, out uint pid);
            if (pid != fgPid) return true;
            inside = true;
            return false;
        }, IntPtr.Zero);
        return inside;
    }

    /// <summary>
    /// Once the focus went into a preview inside the popup, Windows will not report leaving again: poll until the user
    /// is really elsewhere (or back in the popup), then run <paramref name="left"/>.
    /// </summary>
    public static DispatcherTimer LeaveWatch(Window window, Action left)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            if (!window.IsVisible || window.IsActive) { timer.Stop(); return; }
            if (StillInside(window)) return;
            timer.Stop();
            left();
        };
        return timer;
    }
}
