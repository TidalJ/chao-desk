using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ChaoDesk.Host.Launcher;

/// <summary>
/// A small squared icon button for the popups' search bars (preview pane, pin): muted when off, accent on a soft
/// accent fill when on; it dips a little while pressed. Not focusable, so typing stays in the search box.
/// </summary>
internal static class IconButton
{
    public static Border Create(string glyph, string tooltip, Action onClick)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var button = new Border
        {
            Width = 32, Height = 30, CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0, 0, 0),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tooltip, Child = text,
        };
        Set(button, false);
        button.MouseEnter += (_, _) => { if (button.Tag is not true) button.SetResourceReference(Border.BackgroundProperty, "HoverBrush"); };
        button.MouseLeave += (_, _) => { if (button.Tag is not true) button.Background = Brushes.Transparent; Motion.Press(button, false); };
        button.MouseLeftButtonDown += (_, e) => { e.Handled = true; Motion.Press(button, true); }; // not a window drag
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; Motion.Press(button, false); onClick(); };
        return button;
    }

    public static void Set(Border button, bool on, string? glyph = null, string? tooltip = null)
    {
        button.Tag = on;
        if (button.Child is TextBlock text)
        {
            if (glyph != null) text.Text = glyph;
            text.SetResourceReference(TextBlock.ForegroundProperty, on ? "AccentBrush" : "MutedBrush");
        }
        if (tooltip != null) button.ToolTip = tooltip;
        // On: the accent (as the current filter tab's underline) on its soft tint, so it reads at a glance.
        if (on) button.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        else button.Background = Brushes.Transparent;
    }
}
