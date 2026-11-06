using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using ChaoDesk.Host.Desktop;
using ChaoDesk.Host.Launcher;

namespace ChaoDesk.Host.Tests;

public class PopupWidthAnimationTests
{
    [Fact]
    public async Task NativeWidthTracksAnimationAndInterruptedTransitionsCleanUp()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                // A real HWND entirely off screen: no desktop input or activation is needed.
                window = new Window
                {
                    Left = 50, Top = -30000, Width = 300, SizeToContent = SizeToContent.Height,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize, Content = new Border { Height = 240 },
                };
                WindowChrome.SetWindowChrome(window, new WindowChrome
                {
                    CaptionHeight = 0, ResizeBorderThickness = new Thickness(0),
                    GlassFrameThickness = new Thickness(0, 0, 0, 1), UseAeroCaptionButtons = false,
                });
                window.Show();
                Pump(40);
                var hwnd = new WindowInteropHelper(window).Handle;
                var source = HwndSource.FromHwnd(hwnd)!;
                var mode = source.CompositionTarget.RenderMode;
                NativeMethods.GetWindowRect(hwnd, out var initial);
                int completed = 0;
                var widths = new List<int>();
                EventHandler sample = (_, _) =>
                {
                    NativeMethods.GetWindowRect(hwnd, out var rect);
                    widths.Add(rect.Right - rect.Left);
                };
                CompositionTarget.Rendering += sample;
                try
                {
                    PopupWidthAnimation.Resize(window, 600, animate: true, () => completed++);
                    Pump(350);
                }
                finally { CompositionTarget.Rendering -= sample; }
                Assert.Equal(1, completed);
                Assert.Equal(600, window.Width);
                Assert.Equal(mode, source.CompositionTarget.RenderMode);
                Assert.Equal(SizeToContent.Height, window.SizeToContent);
                NativeMethods.GetWindowRect(hwnd, out var expanded);
                Assert.Equal(initial.Left, expanded.Left);
                Assert.Equal(initial.Top, expanded.Top);
                Assert.Equal(initial.Bottom - initial.Top, expanded.Bottom - expanded.Top);
                double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
                Assert.InRange(Math.Abs(expanded.Right - expanded.Left - 600 * scale), 0, 1);
                if (SystemParameters.ClientAreaAnimation)
                    Assert.Contains(widths, w => w > initial.Right - initial.Left && w < expanded.Right - expanded.Left);

                // Reverse before a closing transition completes; its callback must not hide the reopened pane.
                int stale = 0;
                PopupWidthAnimation.Resize(window, 300, animate: true, () => stale++);
                Pump(45);
                double interrupted = window.Width;
                PopupWidthAnimation.Resize(window, 700, animate: true, () => completed++);
                if (SystemParameters.ClientAreaAnimation) Assert.InRange(Math.Abs(window.Width - interrupted), 0, 1);
                Pump(350);
                Assert.Equal(SystemParameters.ClientAreaAnimation ? 0 : 1, stale);
                Assert.Equal(2, completed);
                Assert.Equal(700, window.Width);

                PopupWidthAnimation.Resize(window, 300, animate: true, () => completed++);
                window.Hide();
                Pump(260);
                Assert.Equal(3, completed);
                Assert.Equal(300, window.Width);
                Assert.False(window.HasAnimatedProperties);
                Assert.Equal(mode, source.CompositionTarget.RenderMode);
                Assert.Equal(SizeToContent.Height, window.SizeToContent);

                window.Show();
                PopupWidthAnimation.Resize(window, 600, animate: true, () => stale++);
                PopupWidthAnimation.Resize(window, 380, animate: false);
                Pump(260);
                Assert.Equal(380, window.Width);
                Assert.Equal(SystemParameters.ClientAreaAnimation ? 0 : 2, stale);
                Assert.False(window.HasAnimatedProperties);
                Assert.Equal(mode, source.CompositionTarget.RenderMode);

                PopupWidthAnimation.Resize(window, 600, animate: true);
                window.Close();
                window = null;
                Pump(260); // no callback may access the disposed render target
                result.SetResult();
            }
            catch (Exception ex) { result.SetException(ex); }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
