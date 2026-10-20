using System.Text;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Desktop;

public enum DesktopLayout
{
    Unknown,
    /// <summary>Windows 10 / Windows 11 before 24H2: SHELLDLL_DefView lives in a top-level WorkerW.</summary>
    Legacy,
    /// <summary>Windows 11 24H2+: SHELLDLL_DefView and a child WorkerW both live directly under Progman.</summary>
    Raised,
    /// <summary>Neither pattern matched; we parent to Progman below SHELLDLL_DefView as a last resort.</summary>
    Fallback,
}

/// <summary>Result of locating the window our wallpaper windows are parented to.</summary>
public sealed record DesktopTarget(DesktopLayout Layout, IntPtr Progman, IntPtr DefView, IntPtr Parent, string Branch)
{
    public bool IsValid => Parent != IntPtr.Zero && IsWindow(Parent);

    public override string ToString() =>
        $"layout={Layout}, branch={Branch}, progman=0x{Progman:X}, defView=0x{DefView:X}, parent=0x{Parent:X}";
}

/// <summary>
/// Places windows behind the desktop icons: under a WorkerW on Windows 10 / 11 before 24H2, inside Progman from 24H2
/// on. The parent-finding logic is isolated in <see cref="FindTarget"/>.
/// </summary>
public static class DesktopAttacher
{
    private const uint SpawnWorkerMessage = 0x052C;

    /// <summary>Locate the parent window for the current desktop layout. Never throws.</summary>
    public static DesktopTarget FindTarget()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            Log.Warning("Progman window not found; Explorer may not be running");
            return new DesktopTarget(DesktopLayout.Unknown, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, "none");
        }

        // Ask the shell to create the WorkerW behind the icons. (0xD, 0x1) is the variant newer
        // builds respond to; (0, 0) is the classic form. Sending both is harmless.
        SendMessageTimeout(progman, SpawnWorkerMessage, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);
        SendMessageTimeout(progman, SpawnWorkerMessage, new IntPtr(0xD), new IntPtr(0x1), SMTO_NORMAL, 1000, out _);

        // --- Raised layout (24H2+): DefView and WorkerW are both children of Progman. ---------------
        IntPtr defViewInProgman = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defViewInProgman != IntPtr.Zero)
        {
            IntPtr childWorker = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            if (childWorker != IntPtr.Zero)
            {
                return new DesktopTarget(DesktopLayout.Raised, progman, defViewInProgman, childWorker, "child-of-progman-workerw");
            }
        }

        // --- Legacy layout: find the top-level window hosting DefView, parent = the WorkerW after it. -
        IntPtr hostOfDefView = IntPtr.Zero, defView = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            IntPtr dv = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (dv != IntPtr.Zero && top != progman)
            {
                hostOfDefView = top;
                defView = dv;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (hostOfDefView != IntPtr.Zero)
        {
            IntPtr worker = FindWindowEx(IntPtr.Zero, hostOfDefView, "WorkerW", null);
            if (worker != IntPtr.Zero)
            {
                return new DesktopTarget(DesktopLayout.Legacy, progman, defView, worker, "top-level-workerw-after-defview");
            }
        }

        // --- Fallback: parent to Progman and keep our window below DefView in Z-order. -------------
        return new DesktopTarget(DesktopLayout.Fallback, progman, defViewInProgman != IntPtr.Zero ? defViewInProgman : defView,
            progman, "progman-below-defview");
    }

    /// <summary>
    /// Re-parent <paramref name="hwnd"/> under the desktop target and position it at
    /// <paramref name="screenBounds"/> (physical pixels, virtual-screen coordinates).
    /// </summary>
    public static bool Attach(IntPtr hwnd, DesktopTarget target, PixelRect screenBounds)
    {
        if (!target.IsValid) return false;

        // Convert to a child window before re-parenting so it never shows up as a top-level window.
        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style &= ~(long)(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        style |= WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

        if (SetParent(hwnd, target.Parent) == IntPtr.Zero && GetParent(hwnd) != target.Parent)
        {
            Log.Error("SetParent failed (error {Error})", System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return false;
        }

        Position(hwnd, target, screenBounds);
        return true;
    }

    /// <summary>Move an attached window to new screen bounds (e.g. after a display change).</summary>
    public static void Position(IntPtr hwnd, DesktopTarget target, PixelRect screenBounds)
    {
        // The parent's client origin is the virtual screen's top-left in practice, but mapping through
        // ScreenToClient is exact for every layout (and handles negative monitor coordinates).
        var origin = new POINT(screenBounds.X, screenBounds.Y);
        ScreenToClient(target.Parent, ref origin);

        IntPtr insertAfter = HWND_TOP;
        uint flags = SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW;
        if (target.Layout == DesktopLayout.Fallback)
        {
            // Our window shares Progman with DefView: make sure it sits below the icons.
            insertAfter = HWND_BOTTOM;
        }
        SetWindowPos(hwnd, insertAfter, origin.X, origin.Y, screenBounds.Width, screenBounds.Height, flags);
    }

    /// <summary>Force Explorer to repaint the regular wallpaper (used on exit so no frame is left behind).</summary>
    public static void RefreshDesktop()
    {
        try
        {
            var sb = new StringBuilder(1024);
            string? current = SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0) ? sb.ToString() : null;
            // Re-applying the current path (an empty string for a solid colour) makes Explorer redraw.
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, current ?? string.Empty, SPIF_UPDATEINIFILE);
            Log.Information("Desktop refreshed (wallpaper: {Path})", string.IsNullOrEmpty(current) ? "<solid colour>" : current);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Desktop refresh failed");
        }
    }

    /// <summary>Human-readable description of Progman's children in Z-order, for diagnostics.</summary>
    public static string DescribeProgmanChildren(IntPtr progman)
    {
        var parts = new List<string>();
        EnumChildWindows(progman, (child, _) =>
        {
            if (GetParent(child) == progman) parts.Add($"{GetClassNameOf(child)}(0x{child:X})");
            return true;
        }, IntPtr.Zero);
        return string.Join(" > ", parts);
    }
}
