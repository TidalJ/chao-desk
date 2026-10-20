using System.IO;

namespace ChaoDesk.Host.Tests;

public class SmokeTests
{
    [Fact]
    public void HostAssemblyLoads()
    {
        Assert.NotNull(typeof(App).Assembly);
    }

    /// <summary>The clipboard popup (preview column on and off; a constructor once used a timer before creating it) and the OCR window can be built.</summary>
    [Fact]
    public void PopupWindowsConstruct()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = System.Windows.Application.Current as App ?? new App();
                app.InitializeComponent(); // App.xaml's styles and theme brushes
                string folder = Path.Combine(Path.GetTempPath(), $"chaodesk-clip-{Guid.NewGuid():N}");
                var history = new Clips.ClipboardHistory(folder, 50);
                using var messages = new Desktop.MessageWindow();
                var monitor = new Clips.ClipboardMonitor(messages, history);
                foreach (bool preview in new[] { true, false })
                {
                    var window = new Clips.ClipboardWindow(history, monitor, preview);
                    Assert.Equal(preview, window.PreviewOn);
                    window.Close();
                }
                // The text-recognition window, too (not shown, so it does not start recognising).
                var picture = System.Windows.Media.Imaging.BitmapSource.Create(40, 20, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[40 * 20 * 4], 40 * 4);
                new Ocr.OcrWindow(picture, "auto", false, false, _ => { }, (_, _, _) => { }).Close();
                try { Directory.Delete(folder, true); } catch (IOException) { }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}
