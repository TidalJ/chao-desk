using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ChaoDesk.Host.Launcher;

/// <summary>
/// The filter tabs of the launcher and clipboard popups: segmented pills inside a grey pill (the XAML's container);
/// the current one is filled with the accent, the others are muted text.
/// </summary>
internal static class FilterTab
{
    public static Border Wrap(TextBlock tab) =>
        Pill.Round(new Border { Child = tab, Background = Brushes.Transparent });

    public static void Set(TextBlock tab, bool on)
    {
        tab.Background = Brushes.Transparent;
        tab.SetResourceReference(TextBlock.ForegroundProperty, on ? "OnAccentBrush" : "MutedBrush");
        tab.FontWeight = on ? FontWeights.Medium : FontWeights.Normal;
        tab.TextDecorations = null;
        if (tab.Parent is Border pill)
        {
            if (on) pill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            else pill.Background = Brushes.Transparent;
        }
    }
}

/// <summary>Footer hints ("Enter open · Ctrl+P pin"): key names become small keycaps, the words stay text.</summary>
internal static partial class KeyHints
{
    [GeneratedRegex(@"\b(?:(?:Ctrl|Alt|Shift|Win)\+)*(?:Ctrl|Alt|Shift|Win|Enter|Esc|Tab|Space|Del|Backspace|F\d{1,2}|1…9|[A-Z0-9])\b")]
    private static partial Regex KeyPattern();

    public static void Render(TextBlock target, string text)
    {
        target.Inlines.Clear();
        int at = 0;
        foreach (Match m in KeyPattern().Matches(text))
        {
            if (!LooksLikeKey(m.Value)) continue;
            if (m.Index > at) target.Inlines.Add(new Run(text[at..m.Index]));
            target.Inlines.Add(new InlineUIContainer(Keycap(target, m.Value)) { BaselineAlignment = BaselineAlignment.Center });
            at = m.Index + m.Length;
        }
        if (at < text.Length) target.Inlines.Add(new Run(text[at..]));
    }

    /// <summary>A single capital letter or digit only counts as a key after a modifier ("Ctrl+P"), not on its own.</summary>
    private static bool LooksLikeKey(string value) => value.Length > 1 || value.Contains('+');

    private static Border Keycap(FrameworkElement owner, string keys)
    {
        var text = new TextBlock { Text = keys.Replace("+", " + "), Style = owner.TryFindResource("KeycapText") as Style };
        return new Border { Style = owner.TryFindResource("Keycap") as Style, Child = text, Margin = new Thickness(1, 0, 4, 0) };
    }
}
