using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ChaoDesk.Host.Launcher;

/// <summary>
/// A small squared icon button for the popups' search bars (preview pane, pin): muted when off, ink with a light
/// fill when on. Not focusable, so typing stays in the search box.
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
        button.MouseLeave += (_, _) => { if (button.Tag is not true) button.Background = Brushes.Transparent; };
        button.MouseLeftButtonDown += (_, e) => e.Handled = true; // not a window drag
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return button;
    }

    public static void Set(Border button, bool on, string? glyph = null, string? tooltip = null)
    {
        button.Tag = on;
        if (button.Child is TextBlock text)
        {
            if (glyph != null) text.Text = glyph;
            text.SetResourceReference(TextBlock.ForegroundProperty, on ? "InkBrush" : "MutedBrush");
        }
        if (tooltip != null) button.ToolTip = tooltip;
        if (on) button.SetResourceReference(Border.BackgroundProperty, "NavSelectedBrush");
        else button.Background = Brushes.Transparent;
    }
}
