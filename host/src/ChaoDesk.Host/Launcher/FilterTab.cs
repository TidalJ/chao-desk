using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChaoDesk.Host.Launcher;

/// <summary>
/// The filter tabs of the launcher and clipboard popups: plain text, the current one in ink with an accent underline
/// (no filled chips).
/// </summary>
internal static class FilterTab
{
    public static void Set(TextBlock tab, bool on)
    {
        tab.Background = Brushes.Transparent;
        tab.SetResourceReference(TextBlock.ForegroundProperty, on ? "InkBrush" : "MutedBrush");
        tab.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        tab.TextDecorations = on
            ? new TextDecorationCollection
            {
                new TextDecoration
                {
                    Location = TextDecorationLocation.Underline,
                    Pen = new Pen((Brush)tab.FindResource("AccentBrush"), 2),
                    PenOffset = 4, PenOffsetUnit = TextDecorationUnit.Pixel, PenThicknessUnit = TextDecorationUnit.Pixel,
                },
            }
            : null;
    }
}
