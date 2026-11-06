using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace ChaoDesk.Host.Launcher;

/// <summary>Resizes the real popup so its system border, corners and shadow follow the visible edge.</summary>
internal static class PopupWidthAnimation
{
    private static readonly ConditionalWeakTable<Window, Transition> Active = new();

    public static void Resize(Window window, double width, bool animate, Action? completed = null)
    {
        // Keep the currently displayed width when reversing; the superseded completion must never run.
        if (Active.TryGetValue(window, out var previous)) previous.Stop(finish: false);
        if (!animate || !window.IsVisible || !SystemParameters.ClientAreaAnimation
            || Math.Abs(window.Width - width) < 0.5)
        {
            PopupPlacement.SetWidthKeepingLeft(window, width);
            completed?.Invoke();
            return;
        }

        PopupPlacement.MakeRoomForWidth(window, Math.Max(window.Width, width));
        var transition = new Transition(window, width, completed);
        Active.Add(window, transition);
        transition.Start();
    }

    private sealed class Transition(Window window, double target, Action? completed)
    {
        private readonly SizeToContent _sizeToContent = window.SizeToContent;
        private HwndSource? _source;
        private RenderMode _renderMode;
        private bool _stopped;

        public void Start()
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            if (_source?.CompositionTarget is { } renderer)
            {
                _renderMode = renderer.RenderMode;
                // Use software painting during the resize to reduce GPU surface lag behind the system
                // frame. Restore the original mode as soon as the transition ends.
                renderer.RenderMode = RenderMode.SoftwareOnly;
            }
            window.IsVisibleChanged += OnVisibilityChanged;
            window.Closed += OnClosed;
            var animation = new DoubleAnimation(window.Width, target, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            animation.Completed += (_, _) => Stop(finish: true);
            window.BeginAnimation(FrameworkElement.WidthProperty, animation);
        }

        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!window.IsVisible)
            {
                // Hide finishes synchronizing HWND bounds after this event. Settle on the next dispatch
                // so that synchronization cannot overwrite the requested final width with the old size.
                window.Dispatcher.BeginInvoke(() => Stop(finish: true));
            }
        }

        private void OnClosed(object? sender, EventArgs e) => Stop(finish: false);

        public void Stop(bool finish)
        {
            if (_stopped) return;
            _stopped = true;
            window.IsVisibleChanged -= OnVisibilityChanged;
            window.Closed -= OnClosed;
            Active.Remove(window);
            double width = finish ? target : window.Width;
            // Set the base value while the animation still holds the displayed value, then detach its clock.
            window.Width = width;
            window.BeginAnimation(FrameworkElement.WidthProperty, null);
            // HWND size notifications may coerce the base value while a clock is attached (especially
            // when hidden). Commit once more after removing it so the requested width is authoritative.
            window.Width = width;
            window.SizeToContent = _sizeToContent;
            if (_source is { IsDisposed: false, CompositionTarget: { } renderer })
                renderer.RenderMode = _renderMode;
            if (finish) completed?.Invoke();
        }
    }
}
