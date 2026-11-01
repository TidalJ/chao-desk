using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Serilog;

namespace ChaoDesk.Host.Panel;

/// <summary>Live DWM backdrop, not a captured bitmap. Windows owns background updates and drag composition.</summary>
internal static class NativePanelBackdrop
{
    internal const int Attribute = 38; // DWMWA_SYSTEMBACKDROP_TYPE
    internal const int None = 1;

    internal static int RequestedType(string material, bool highContrast, int windowsBuild) =>
        None;

    public static bool Apply(Window window, string material, bool dark)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        int type = RequestedType(material, SystemParameters.HighContrast, Environment.OSVersion.Version.Build);
        int theme = dark ? 1 : 0, corner = 1; // WPF owns the single adjustable silhouette.
        int borderColor = unchecked((int)0xfffffffe); // DWMWA_COLOR_NONE: no system accent border.
        DwmSetWindowAttribute(hwnd, 20, ref theme, sizeof(int));
        DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34, ref borderColor, sizeof(int));
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        int frame = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        int result = DwmSetWindowAttribute(hwnd, Attribute, ref type, sizeof(int));
        // Clear/soft deliberately disable the system material: Acrylic adds its own opaque tint,
        // even when every WPF brush above it is transparent.
        bool live = !SystemParameters.HighContrast && frame >= 0 && result >= 0;
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } composition)
            composition.BackgroundColor = live ? Colors.Transparent : dark ? Color.FromRgb(34, 42, 51) : Color.FromRgb(237, 242, 246);
        Log.Information("Panel backdrop: live={Live}; type={Type}; frame=0x{Frame:X8}; result=0x{Result:X8}; layered={Layered}", live, type, frame, result, window.AllowsTransparency);
        return live;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
