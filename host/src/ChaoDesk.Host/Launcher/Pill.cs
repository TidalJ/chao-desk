using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ChaoDesk.Host.Launcher;

/// <summary>
/// Turns a height into the corner radius of a true pill (half the height). WPF does not shrink an oversized radius the
/// way CSS does — a radius larger than half the height draws pointed, lens-shaped ends — so pills bind to their height.
/// </summary>
public sealed class PillRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double h && h > 0 ? new CornerRadius(h / 2) : new CornerRadius(0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

internal static class Pill
{
    private static readonly PillRadiusConverter Converter = new();

    /// <summary>Gives a border built in code fully round ends, whatever its height.</summary>
    public static T Round<T>(T border) where T : Border
    {
        border.SetBinding(Border.CornerRadiusProperty, new Binding(nameof(FrameworkElement.ActualHeight))
        {
            RelativeSource = RelativeSource.Self, Converter = Converter,
        });
        return border;
    }
}
