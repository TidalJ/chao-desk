using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChaoDesk.Host.Desktop;
using ChaoDesk.Host.Localization;
using Microsoft.Win32;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Capture;

/// <summary>How screenshots are saved (from the settings).</summary>
public sealed record ScreenshotOptions(string Folder, bool Jpeg, int JpegQuality, bool AutoSave, bool QuickSaves);

/// <summary>
/// Screenshots, Snipaste style: region capture with annotations (Shift+F2 by default), quick capture of the screen under
/// the mouse (Shift+F1), pinning images to the screen (Shift+F3), and scrolling capture (Shift+F4 or the toolbar). Copies go to
/// the clipboard as lossless images, so they also land in the clipboard history. UI thread only.
/// </summary>
public sealed class ScreenshotService
{
    private readonly Func<ScreenshotOptions> _options;
    private List<CaptureOverlay>? _overlays;

    public PinManager Pins { get; }

    /// <summary>Reads the text in a captured area or a pin (set by the app: opens the text window).</summary>
    public Action<BitmapSource>? Recognize { get; set; }

    public ScreenshotService(Func<ScreenshotOptions> options)
    {
        _options = options;
        Pins = new PinManager(Copy, SaveAs) { Recognize = image => Recognize?.Invoke(image) };
    }

    // ---- Region capture ----------------------------------------------------------------------------------

    public void StartRegion(CaptureMode mode = CaptureMode.Copy)
    {
        if (_overlays != null) return;
        var shot = ScreenCapture.Grab(ScreenCapture.VirtualScreen); // before any of our windows appear
        var targets = ScreenCapture.SnapTargets();
        GetCursorPos(out POINT cursor);
        _overlays = new List<CaptureOverlay>();
        CaptureOverlay? active = null;
        foreach (var monitor in MonitorInfo.GetAll())
        {
            var overlay = new CaptureOverlay(shot, targets, monitor, mode, OnFinished);
            _overlays.Add(overlay);
            overlay.Show();
            if (monitor.Bounds.Contains(cursor.X, cursor.Y)) active = overlay;
        }
        active ??= _overlays[0];
        active.Activate();
        SetForegroundWindow(new WindowInteropHelper(active).Handle);
    }

    /// <summary>The pin hotkey while capturing: pin the current selection.</summary>
    public bool PinCurrentSelection()
    {
        var overlay = _overlays?.FirstOrDefault(o => o.HasSelection);
        if (overlay == null) return false;
        overlay.Finish(CaptureAction.Pin);
        return true;
    }

    private void OnFinished(CaptureOverlay overlay, CaptureAction action)
    {
        BitmapSource? image = action is CaptureAction.Copy or CaptureAction.Save or CaptureAction.Pin or CaptureAction.Ocr && overlay.HasSelection ? overlay.Render() : null;
        var selection = overlay.Selection;
        foreach (var o in _overlays ?? []) o.Close();
        _overlays = null;
        try
        {
            switch (action)
            {
                case CaptureAction.Copy when image != null:
                    Copy(image);
                    if (_options().AutoSave) Save(image);
                    break;
                case CaptureAction.Save when image != null:
                    SaveAs(image, null);
                    break;
                case CaptureAction.Pin when image != null:
                    Pin(image, selection);
                    break;
                case CaptureAction.Ocr when image != null:
                    Recognize?.Invoke(image);
                    break;
                case CaptureAction.Scroll when overlay.HasSelection:
                    _ = ScrollCaptureAsync(selection);
                    break;
            }
        }
        catch (Exception ex) { Log.Error(ex, "Screenshot {Action} failed", action); }
    }

    // ---- Quick capture and pins ---------------------------------------------------------------------------

    /// <summary>The whole screen under the mouse, straight to the clipboard (and to a file, if set).</summary>
    public void QuickCapture()
    {
        GetCursorPos(out POINT cursor);
        var monitor = MonitorInfo.GetAll().FirstOrDefault(m => m.Bounds.Contains(cursor.X, cursor.Y)) ?? MonitorInfo.GetAll()[0];
        var image = ScreenCapture.Grab(monitor.Bounds).ToBitmap();
        Copy(image);
        string? file = _options().QuickSaves ? Save(image) : null;
        Toast.Show(Loc.T("Screenshot copied"), file != null ? Loc.F("Saved to {0}", file) : $"{image.PixelWidth} × {image.PixelHeight}", image,
            file != null ? () => RevealInExplorer(file) : null);
    }

    /// <summary>The pin hotkey: choose an area to pin (during a capture: pin its selection).</summary>
    public void StartPin()
    {
        if (PinCurrentSelection()) return;
        StartRegion(CaptureMode.Pin);
    }

    /// <summary>Pin what is on the clipboard (an image, or text drawn as an image) at the mouse.</summary>
    public void PinClipboard()
    {
        var image = ClipboardImage(allowText: true);
        if (image == null) { Toast.Show(Loc.T("Nothing to pin"), Loc.T("Copy an image or some text first.")); return; }
        PinAtMouse(image);
    }

    /// <summary>Pins an image with its corner at the mouse.</summary>
    public void PinAtMouse(BitmapSource image)
    {
        GetCursorPos(out POINT cursor);
        Pin(image, new PixelRect(cursor.X, cursor.Y, image.PixelWidth, image.PixelHeight));
    }

    /// <summary>The image on the clipboard (PNG first, lossless), or its text drawn as one; null if neither.</summary>
    public BitmapSource? ClipboardImage(bool allowText)
    {
        BitmapSource? image = null;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data?.GetDataPresent("PNG") == true && data.GetData("PNG") is MemoryStream png)
                image = BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            else if (Clipboard.ContainsImage()) image = Clipboard.GetImage();
            else if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: 1 } files && LoadPicture(files[0]!) is { } picture) image = picture;
            else if (allowText && Clipboard.ContainsText()) image = RenderText(Clipboard.GetText());
        }
        catch (COMException) { return null; }
        image?.Freeze();
        return image;
    }

    private static BitmapSource? LoadPicture(string path)
    {
        try { return BitmapFrame.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad); }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or FileFormatException or ArgumentException) { return null; }
    }

    private void Pin(BitmapSource image, PixelRect at) => Pins.Add(image, at);

    /// <summary>Clipboard text as a picture (like Snipaste), at the scale of the monitor under the mouse.</summary>
    private static BitmapSource RenderText(string text)
    {
        if (text.Length > 4000) text = text[..4000] + "…";
        GetCursorPos(out POINT cursor);
        double scale = MonitorInfo.GetAll().FirstOrDefault(m => m.Bounds.Contains(cursor.X, cursor.Y))?.Scale ?? 1;
        var block = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(12, 10, 12, 10),
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 560,
                FontSize = 14,
                FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28)),
            },
        };
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        block.Arrange(new Rect(block.DesiredSize));
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(block.ActualWidth * scale), (int)Math.Ceiling(block.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(block);
        return bmp;
    }

    // ---- Scrolling capture -----------------------------------------------------------------------------------

    private async Task ScrollCaptureAsync(PixelRect region)
    {
        if (region.Width < 50 || region.Height < 50) { Toast.Show(Loc.T("Area too small"), Loc.T("Select a larger area for a scrolling capture.")); return; }
        var frame = new ScrollFrame(region);
        frame.Show();
        GetCursorPos(out POINT before);
        var stitcher = new ScrollStitcher(region.Width, region.Height);
        try
        {
            await Task.Delay(150); // let the overlay disappear
            stitcher.Add(ScreenCapture.Grab(region).Data);
            int idle = 0;
            int notch = region.Height >= 400 ? -240 : -120;
            for (int step = 0; step < 400 && !frame.Stopped && !stitcher.IsFull; step++)
            {
                SetCursorPos(region.X + region.Width / 2, region.Y + region.Height / 2);
                var wheel = new[] { new MOUSE_INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_WHEEL, mouseData = notch } } };
                SendInput(1, wheel, Marshal.SizeOf<MOUSE_INPUT>());
                await Task.Delay(260); // smooth scrolling settles
                if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) break; // Esc
                if (stitcher.Add(ScreenCapture.Grab(region).Data) == 0) { if (++idle >= 3) break; }
                else idle = 0;
                frame.SetProgress(stitcher.Height);
            }
        }
        finally
        {
            frame.Close();
            SetCursorPos(before.X, before.Y);
        }
        var pixels = stitcher.Compose();
        var image = BitmapSource.Create(region.Width, stitcher.Height, 96, 96, PixelFormats.Bgr32, null, pixels, region.Width * 4);
        image.Freeze();
        Copy(image);
        string? file = Save(image);
        Toast.Show(Loc.T("Long screenshot copied"), file != null ? Loc.F("{0} × {1} · saved to {2}", region.Width, stitcher.Height, file) : $"{region.Width} × {stitcher.Height}", image,
            file != null ? () => RevealInExplorer(file) : null);
    }

    /// <summary>The outline around a scrolling capture, with its Stop button (outside the area, so it is never captured).</summary>
    private sealed class ScrollFrame : Window
    {
        private readonly TextBlock _status = new() { Foreground = Brushes.White, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        private readonly Window _panel;
        public bool Stopped { get; private set; }

        public ScrollFrame(PixelRect r)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x6A, 0xF0)), BorderThickness = new Thickness(2) };
            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
                SetWindowPos(hwnd, new IntPtr(-1), r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            };

            var stop = new Button { Content = Loc.T("Stop"), Padding = new Thickness(12, 2, 12, 3) };
            stop.Click += (_, _) => Stopped = true;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_status);
            row.Children.Add(stop);
            _status.Text = Loc.T("Scrolling… Esc or Stop to finish");
            _panel = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                ShowActivated = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1F, 0x23, 0x28)),
                Content = new Border { Padding = new Thickness(10, 6, 6, 6), Child = row },
            };
            _panel.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(_panel).Handle;
                int below = r.Bottom + 8;
                var screen = ScreenCapture.VirtualScreen;
                int y = below + 50 < screen.Bottom ? below : Math.Max(screen.Y, r.Y - 50);
                SetWindowPos(hwnd, new IntPtr(-1), r.X, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            };
            Loaded += (_, _) => _panel.Show();
            Closed += (_, _) => _panel.Close();
        }

        public void SetProgress(int height) => _status.Text = Loc.F("Scrolling… {0:N0} px · Esc or Stop to finish", height);
    }

    // ---- Clipboard and files ----------------------------------------------------------------------------------

    /// <summary>Puts a lossless image on the clipboard (the DIB plus a "PNG" stream that keeps alpha).</summary>
    public void Copy(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image);
        data.SetData("PNG", new MemoryStream(Encode(image, jpeg: false, 100)));
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try { Clipboard.SetDataObject(data, copy: true); return; }
            catch (COMException) { Thread.Sleep(30); }
        }
        Log.Warning("Could not put the screenshot on the clipboard");
    }

    /// <summary>Saves into the screenshot folder with a timestamped name; returns the path (null on failure).</summary>
    public string? Save(BitmapSource image)
    {
        var o = _options();
        try
        {
            Directory.CreateDirectory(o.Folder);
            string path = Path.Combine(o.Folder, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}{(o.Jpeg ? ".jpg" : ".png")}");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(o.Folder, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss} ({i}){(o.Jpeg ? ".jpg" : ".png")}");
            File.WriteAllBytes(path, Encode(image, o.Jpeg, o.JpegQuality));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not save a screenshot to {Folder}", o.Folder);
            return null;
        }
    }

    public void SaveAs(BitmapSource image, Window? owner)
    {
        var o = _options();
        Directory.CreateDirectory(o.Folder);
        var dlg = new SaveFileDialog
        {
            Title = Loc.T("Save screenshot"),
            InitialDirectory = o.Folder,
            FileName = $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}",
            Filter = Loc.T("PNG image (lossless)|*.png|JPEG image (smaller)|*.jpg"),
            FilterIndex = o.Jpeg ? 2 : 1,
        };
        if (dlg.ShowDialog(owner) != true) return;
        bool jpeg = dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || dlg.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
        try { File.WriteAllBytes(dlg.FileName, Encode(image, jpeg, o.JpegQuality)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner!, Loc.F("Could not save the screenshot:\n\n{0}", ex.Message), "Chao Desk",
 MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static byte[] Encode(BitmapSource image, bool jpeg, int quality)
    {
        BitmapEncoder encoder = jpeg
            ? new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 30, 100) }
            : new PngBitmapEncoder();
        var frame = jpeg ? new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0) : (BitmapSource)image;
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static void RevealInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
}
