using System.Windows;
using System.Windows.Controls;

namespace ChaoDesk.Host.Settings;

/// <summary>
/// Lays out a settings row (a Grid with the SettingRow style): the label and hint take whatever width the control on
/// the right leaves, instead of a fixed right margin that wrapped short hints early and left a few characters alone on
/// the last line. The first StackPanel is the text; every other child goes in an Auto column on the right.
/// </summary>
public static class SettingRowLayout
{
    public static readonly DependencyProperty AutoProperty = DependencyProperty.RegisterAttached(
        "Auto", typeof(bool), typeof(SettingRowLayout), new PropertyMetadata(false, OnAutoChanged));

    public static bool GetAuto(DependencyObject d) => (bool)d.GetValue(AutoProperty);
    public static void SetAuto(DependencyObject d, bool value) => d.SetValue(AutoProperty, value);

    private static void OnAutoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid || e.NewValue is not true) return;
        if (grid.IsLoaded) Arrange(grid);
        else grid.Loaded += (_, _) => Arrange(grid);
    }

    /// <summary>Lays out every row under <paramref name="root"/> now (for rendering a window that is never shown).</summary>
    public static void ArrangeAll(DependencyObject root)
    {
        if (root is Grid g && GetAuto(g)) Arrange(g);
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) ArrangeAll(child);
    }

    private static void Arrange(Grid grid)
    {
        if (grid.ColumnDefinitions.Count > 0) return; // laid out by hand
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = grid.Children.OfType<StackPanel>().FirstOrDefault();
        foreach (UIElement child in grid.Children)
        {
            if (child == text)
            {
                Grid.SetColumn(child, 0);
                // Only one child: let it use the whole row.
                if (grid.Children.Count == 1) Grid.SetColumnSpan(child, 2);
                else text.Margin = new Thickness(0, 0, 24, 0);
            }
            else Grid.SetColumn(child, 1);
        }
    }
}
