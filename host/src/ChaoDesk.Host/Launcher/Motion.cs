using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ChaoDesk.Host.Desktop;

namespace ChaoDesk.Host.Launcher;

/// <summary>
/// The small transitions that make the popups feel calm: everything is 100–180 ms, eases out, and starts from a
/// partly visible state, so nothing ever blinks to empty. Transform and opacity only (cheap to render).
/// </summary>
internal static class Motion
{
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Fast at first and settling softly, like a damped spring (for things that move across the window).</summary>
    private static readonly IEasingFunction Settle = new QuinticEase { EasingMode = EasingMode.EaseOut };

    /// <summary>A popup opening: content scales up from 98 % and fades in.</summary>
    public static void Open(FrameworkElement content)
    {
        content.RenderTransformOrigin = new Point(0.5, 0.2);
        var scale = new ScaleTransform(0.98, 0.98);
        content.RenderTransform = scale;
        content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.98, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = Ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.98, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = Ease });
    }

    /// <summary>New content in a panel: slides 8 px from the right and fades up from 30 %.</summary>
    public static void SlideIn(FrameworkElement element)
    {
        var move = new TranslateTransform(8, 0);
        element.RenderTransform = move;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(150)));
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = Ease });
    }

    /// <summary>A panel appearing where the window just grew: fades in while drifting 12 px from the right.</summary>
    public static void Reveal(FrameworkElement element)
    {
        var move = new TranslateTransform(12, 0);
        element.RenderTransform = move;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = Ease });
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = Settle });
    }

    private sealed class Glide { public int Visible; public object Token = new(); }
    private static readonly ConditionalWeakTable<Window, Glide> Glides = new();

    /// <summary>
    /// Animates a window's width (left edge fixed) without the judder of resizing it every frame: the window is
    /// resized once — before a widening, after a narrowing — and the frames only crop it with a window region,
    /// which the compositor applies without WPF laying out or redrawing anything. While the region is set Windows
    /// draws no shadow and square-ish corners; both return at the end. Interrupted, a glide carries on from where
    /// the last one got to. When the wider window would leave the screen it just changes size at once.
    /// </summary>
    public static void GlideWidth(Window window, double width, Action? done = null)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !window.IsVisible)
        {
            window.Width = width;
            done?.Invoke();
            return;
        }
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        NativeMethods.GetWindowRect(hwnd, out var rect);
        int windowWidth = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        int target = (int)Math.Round(width * scale);
        var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        if (target > windowWidth && NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST), ref info)
            && rect.Left + target > info.rcWork.Right)
        {
            PopupPlacement.SetWidthKeepingLeft(window, width); // no room to grow to the right: move over instead
            done?.Invoke();
            return;
        }

        var glide = Glides.GetOrCreateValue(window);
        int from = glide.Token is Stopwatch ? glide.Visible : windowWidth; // a glide in progress: from where it is
        var clock = Stopwatch.StartNew();
        glide.Token = clock;
        int radius = (int)Math.Round(16 * scale);
        var sizeToContent = window.SizeToContent; // a resize from outside could turn it off
        void Crop(int visible)
        {
            glide.Visible = visible;
            NativeMethods.GetWindowRect(hwnd, out var now); // the height may follow the content meanwhile
            NativeMethods.SetWindowRgn(hwnd, NativeMethods.CreateRoundRectRgn(0, 0, visible + 1, now.Bottom - now.Top + 1, radius, radius), true);
        }
        Crop(from);
        if (target > windowWidth)
        {
            // Wider: take the full size now, hidden behind the crop.
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, target, height, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            window.Width = width;
            window.SizeToContent = sizeToContent;
        }
        EventHandler? frame = null;
        frame = (_, _) =>
        {
            if (glide.Token != clock) { CompositionTarget.Rendering -= frame; return; } // a newer glide took over
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / 300);
            double eased = 1 - Math.Pow(1 - t, 4);
            Crop((int)Math.Round(from + (target - from) * eased));
            if (t < 1) return;
            CompositionTarget.Rendering -= frame;
            glide.Token = new object();
            NativeMethods.GetWindowRect(hwnd, out var now);
            if (now.Right - now.Left > target)
            {
                // Narrower: the window takes the small size only now (the crop already shows exactly that).
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, now.Left, now.Top, target, now.Bottom - now.Top, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                window.Width = width;
                window.SizeToContent = sizeToContent;
            }
            NativeMethods.SetWindowRgn(hwnd, IntPtr.Zero, true); // shadow and rounded corners back
            done?.Invoke();
        };
        CompositionTarget.Rendering += frame;
    }

    /// <summary>Content that arrived after a wait: fades up from 40 %.</summary>
    public static void FadeIn(FrameworkElement element) =>
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(140)));

    /// <summary>A button held down: shrinks a little, and springs back on release.</summary>
    public static void Press(FrameworkElement element, bool down)
    {
        if (element.RenderTransform is not ScaleTransform scale)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = scale = new ScaleTransform(1, 1);
        }
        var to = new DoubleAnimation(down ? 0.88 : 1, TimeSpan.FromMilliseconds(down ? 70 : 160))
        {
            EasingFunction = down ? Ease : new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, to);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, to);
    }

    /// <summary>A section appearing below the search box: its height grows from 0 (the window follows) while it fades in.</summary>
    public static void Expand(FrameworkElement element, double height)
    {
        element.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(0, height, TimeSpan.FromMilliseconds(170)) { EasingFunction = Ease });
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
    }
}
