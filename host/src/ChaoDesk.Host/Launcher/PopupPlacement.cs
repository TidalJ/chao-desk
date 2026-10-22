using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Launcher;

/// <summary>Shared behaviour of the launcher and clipboard popups: placement, rounded corners, focus.</summary>
internal static class PopupPlacement
{
    /// <summary>
    /// Centres the window horizontally on the monitor under the mouse, <paramref name="topFraction"/> of the way
    /// down its work area. Works in physical pixels so mixed-DPI setups land correctly; WPF rescales the window
    /// itself when it arrives on a monitor with another scale.
    /// </summary>
    public static void PlaceOnActiveMonitor(Window window, double topFraction)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        GetCursorPos(out POINT cursor);
        IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpi, out _);
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var work = info.rcWork;
        int width = (int)Math.Round(window.Width * scale);
        int x = work.Left + (work.Right - work.Left - width) / 2;
        int y = work.Top + (int)((work.Bottom - work.Top) * topFraction);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Changes the window's width in one step, keeping its left edge (what is on the left stays put) unless the
    /// wider window would leave the monitor's work area — then it moves left just enough.
    /// </summary>
    public static void SetWidthKeepingLeft(Window window, double width)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !window.IsVisible) { window.Width = width; return; }
        GetWindowRect(hwnd, out RECT r);
        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info)) { window.Width = width; return; }
        int w = (int)Math.Round(width * VisualTreeHelper.GetDpi(window).DpiScaleX);
        int x = Math.Max(info.rcWork.Left, Math.Min(r.Left, info.rcWork.Right - w));
        SetWindowPos(hwnd, IntPtr.Zero, x, r.Top, w, r.Bottom - r.Top, SWP_NOZORDER | SWP_NOACTIVATE);
        window.Width = width; // WPF's own value (already the window's size: no second step)
    }

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_KEYMENU = 0xF100;

    /// <summary>
    /// The popups open on hotkeys that usually include Alt (Alt+Space). Releasing Alt in the new window would put
    /// it in menu mode — Windows' window menu and WPF's access-key mode — which swallows the next key, so the
    /// first letter typed went missing. A lone Alt press/release is ignored here.
    /// </summary>
    public static void IgnoreAltMenu(Window window)
    {
        window.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.System && e.SystemKey is System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt) e.Handled = true; };
        window.PreviewKeyUp += (_, e) => { if (e.Key == System.Windows.Input.Key.System && e.SystemKey is System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt) e.Handled = true; };
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_KEYMENU) handled = true;
            return IntPtr.Zero;
        });
    }

    /// <summary>Windows 11 rounded corners and the system shadow for a borderless window.</summary>
    public static void RoundCorners(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    /// <summary>Show and take the keyboard focus (we may do so: we just received the hotkey).</summary>
    public static void ShowActivated(Window window)
    {
        window.Show();
        window.Activate();
        SetForegroundWindow(new WindowInteropHelper(window).Handle);
    }
}
