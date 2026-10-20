using System.Runtime.InteropServices;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Desktop;

/// <summary>
/// The file selected in an Explorer window or on the desktop, through the Shell.Application automation object
/// (late bound). Windows 11 tabs share one top-level window; the active tab is the visible ShellTabWindowClass
/// child, matched through each tab's IShellBrowser window. UI thread (STA) only.
/// </summary>
public static class ExplorerSelection
{
    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;

    private static dynamic? _shell;

    /// <summary>The focused (or first selected) item's path in <paramref name="window"/>, or null.</summary>
    public static string? SelectedPath(IntPtr window)
    {
        try
        {
            _shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            dynamic? view = GetClassNameOf(window) is "Progman" or "WorkerW" ? DesktopView() : WindowView(window);
            if (view == null) return null;
            dynamic? item = null;
            try { item = view.Document.FocusedItem; } catch (COMException) { }
            if (item != null)
            {
                // The focused item only counts if it is selected (a focus rectangle alone is not a choice).
                bool selected = false;
                foreach (dynamic s in view.Document.SelectedItems()) { if (s.Path == item.Path) { selected = true; break; } }
                if (!selected) item = null;
            }
            if (item == null)
                foreach (dynamic s in view.Document.SelectedItems()) { item = s; break; }
            return item == null ? null : (string)item.Path;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Log.Debug(ex, "Could not read the Explorer selection");
            _shell = null;
            return null;
        }
    }

    private static dynamic? WindowView(IntPtr window)
    {
        IntPtr activeTab = IntPtr.Zero;
        for (IntPtr tab = FindWindowEx(window, IntPtr.Zero, "ShellTabWindowClass", null); tab != IntPtr.Zero; tab = FindWindowEx(window, tab, "ShellTabWindowClass", null))
            if (IsWindowVisible(tab)) { activeTab = tab; break; }

        dynamic? fallback = null;
        foreach (dynamic w in _shell!.Windows())
        {
            long hwnd;
            try { hwnd = (long)w.HWND; } catch (COMException) { continue; }
            if (hwnd != window.ToInt64()) continue;
            fallback ??= w;
            if (activeTab == IntPtr.Zero || TabOf(w) == activeTab) return w;
        }
        return fallback;
    }

    /// <summary>The tab window behind a shell window object (its IShellBrowser's window).</summary>
    private static IntPtr TabOf(object shellWindow)
    {
        if (shellWindow is not ShellInterop.IServiceProvider sp) return IntPtr.Zero;
        if (sp.QueryService(ShellInterop.SID_STopLevelBrowser, ShellInterop.IID_IShellBrowser, out IntPtr ptr) != 0 || ptr == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            var browser = (ShellInterop.IShellBrowser)Marshal.GetObjectForIUnknown(ptr);
            return browser.GetWindow(out IntPtr hwnd) == 0 ? hwnd : IntPtr.Zero;
        }
        finally { Marshal.Release(ptr); }
    }

    private static dynamic? DesktopView()
    {
        object location = 0; // CSIDL_DESKTOP
        object root = Type.Missing;
        int hwnd = 0;
        return _shell!.Windows().FindWindowSW(ref location, ref root, SWC_DESKTOP, ref hwnd, SWFO_NEEDDISPATCH);
    }
}
