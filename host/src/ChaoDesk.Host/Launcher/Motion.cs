using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ChaoDesk.Host.Launcher;

/// <summary>
/// The small transitions that make the popups feel calm: everything is 100–180 ms, eases out, and starts from a
/// partly visible state, so nothing ever blinks to empty. Transform and opacity only (cheap to render).
/// </summary>
internal static class Motion
{
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

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

    /// <summary>A section appearing below the search box: its height grows from 0 (the window follows) while it fades in.</summary>
    public static void Expand(FrameworkElement element, double height)
    {
        element.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(0, height, TimeSpan.FromMilliseconds(170)) { EasingFunction = Ease });
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
    }
}
