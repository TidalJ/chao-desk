using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChaoDesk.Host.Desktop;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Launcher;

/// <summary>Icons for shell items and files, as frozen WPF images (safe to hand between threads).</summary>
internal static class ShellIcons
{
    public const int Size = 48;

    /// <summary>The icon of a shell item (an app in AppsFolder, a file, a folder). Call on an STA thread.</summary>
    public static ImageSource? ForShellItem(ShellInterop.IShellItem item)
    {
        if (item is not ShellInterop.IShellItemImageFactory factory) return null;
        if (factory.GetImage(new ShellInterop.SIZE(Size, Size), ShellInterop.SIIGBF.ICONONLY, out IntPtr hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
        try { return FromHBitmap(hbitmap); }
        finally { DeleteObject(hbitmap); }
    }

    /// <summary>
    /// The shell's thumbnail of a file (the picture Explorer shows in large-icon view: a video frame, a page, a
    /// photo through an installed codec), or its icon when there is none. Call on an STA thread.
    /// </summary>
    public static ImageSource? Thumbnail(string path, int size)
    {
        try
        {
            var item = (ShellInterop.IShellItem)SHCreateItemFromParsingName(path, IntPtr.Zero, ShellInterop.IID_IShellItem);
            try
            {
                if (item is not ShellInterop.IShellItemImageFactory factory) return null;
                if (factory.GetImage(new ShellInterop.SIZE(size, size), ShellInterop.SIIGBF.RESIZETOFIT, out IntPtr hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
                try { return FromHBitmap(hbitmap); }
                finally { DeleteObject(hbitmap); }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>The icon of a path through the shell (exact icon for .exe/.lnk, folders and files).</summary>
    public static ImageSource? ForPath(string path)
    {
        try
        {
            var item = (ShellInterop.IShellItem)SHCreateItemFromParsingName(path, IntPtr.Zero, ShellInterop.IID_IShellItem);
            try { return ForShellItem(item); }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>The generic icon for a file type (no disk access), e.g. ".mp4"; "\" for a folder.</summary>
    public static ImageSource? ForExtension(string extension)
    {
        var info = new SHFILEINFO();
        bool folder = extension == "\\";
        uint attributes = folder ? 0x10u : FILE_ATTRIBUTE_NORMAL;
        string name = folder ? "folder" : "file" + extension;
        if (SHGetFileInfo(name, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DestroyIcon(info.hIcon); }
    }

    /// <summary>A 32-bit shell HBITMAP (premultiplied alpha) as a top-down BitmapSource.</summary>
    private static BitmapSource? FromHBitmap(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight == 0) return null;
        int width = bm.bmWidth, height = Math.Abs(bm.bmHeight);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }

        // Some icons come without alpha at all; show them opaque rather than invisible.
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length && !anyAlpha; i += 4) anyAlpha = pixels[i] != 0;
        if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        bmp.Freeze();
        return bmp;
    }
}
