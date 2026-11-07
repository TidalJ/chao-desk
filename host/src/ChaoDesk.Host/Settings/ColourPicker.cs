using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using ChaoDesk.Host.Localization;

namespace ChaoDesk.Host.Settings;

/// <summary>
/// A colour picker: a saturation/brightness field to drag in, a hue bar under it and a box for typing a colour.
/// <see cref="Moved"/> fires on every change while dragging or typing, so the caller can show the colour live.
/// </summary>
internal sealed class ColourPicker : StackPanel
{
    private const double FieldWidth = 248, FieldHeight = 156, HueHeight = 14;

    private double _hue, _saturation, _value;
    private bool _syncing;
    private readonly SolidColorBrush _fieldHue = new();
    private readonly SolidColorBrush _current = new();
    private readonly SolidColorBrush _pureHue = new();
    private readonly Canvas _fieldLayer = new() { Width = FieldWidth, Height = FieldHeight };
    private readonly Canvas _hueLayer = new() { Width = FieldWidth, Height = HueHeight };
    private readonly Ellipse _fieldThumb, _hueThumb;
    private readonly TextBox _hexBox;

    public event Action<Color>? Moved;

    public Color Color { get; private set; }

    public ColourPicker(Color start, Style fieldStyle)
    {
        Width = FieldWidth;

        // The field: the hue, whitened to the left, darkened to the bottom.
        var field = new Grid { Width = FieldWidth, Height = FieldHeight, Cursor = Cursors.Cross };
        var corner = new CornerRadius(10);
        field.Children.Add(new Border { CornerRadius = corner, Background = _fieldHue });
        field.Children.Add(new Border { CornerRadius = corner, Background = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) });
        field.Children.Add(new Border { CornerRadius = corner, Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) });
        _fieldThumb = Thumb(18, _current);
        _fieldLayer.Children.Add(_fieldThumb);
        field.Children.Add(_fieldLayer);
        Wire(field, p => SetFromField(p));
        Children.Add(field);

        // The hue bar.
        var hues = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++) hues.GradientStops.Add(new GradientStop(FromHsv(i * 60 % 360, 1, 1), i / 6.0));
        var bar = new Grid { Width = FieldWidth, Height = HueHeight, Margin = new Thickness(0, 16, 0, 0), Cursor = Cursors.Hand };
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(HueHeight / 2), Background = hues });
        _hueThumb = Thumb(20, _pureHue);
        _hueLayer.Children.Add(_hueThumb);
        bar.Children.Add(_hueLayer);
        Wire(bar, p => SetFromHue(p));
        Children.Add(bar);

        // The colour itself and a box to type one.
        var row = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = true };
        var sample = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = _current, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0) };
        sample.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        row.Children.Add(sample);
        _hexBox = new TextBox { Style = fieldStyle, MaxLength = 7, Margin = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = Loc.T("Type a colour as #RRGGBB, for example #3366CC.") };
        _hexBox.TextChanged += (_, _) =>
        {
            if (_syncing || !ThemeManager.TryParseAccent(_hexBox.Text, out var typed) || _hexBox.Text.Trim().TrimStart('#').Length != 6) return;
            SetColor(typed, fromBox: true);
            Moved?.Invoke(Color);
        };
        _hexBox.GotKeyboardFocus += (_, _) => _hexBox.SelectAll();
        row.Children.Add(_hexBox);
        Children.Add(row);

        Loaded += (_, _) => PlaceThumbs();
        SetColor(start, fromBox: false);
    }

    /// <summary>Puts the keyboard in the colour box (typing a colour is the other way in).</summary>
    public void FocusBox() => _hexBox.Focus();

    private static Ellipse Thumb(double size, Brush fill) => new()
    {
        Width = size, Height = size, Fill = fill, Stroke = Brushes.White, StrokeThickness = 3, IsHitTestVisible = false,
        Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.35, Color = Colors.Black },
    };

    /// <summary>Press, drag (the mouse is captured, so it may leave the area) and let go.</summary>
    private static void Wire(FrameworkElement area, Action<Point> at)
    {
        area.MouseLeftButtonDown += (_, e) => { area.CaptureMouse(); at(e.GetPosition(area)); e.Handled = true; };
        area.MouseMove += (_, e) => { if (area.IsMouseCaptured) at(e.GetPosition(area)); };
        area.MouseLeftButtonUp += (_, e) => { area.ReleaseMouseCapture(); e.Handled = true; };
    }

    private void SetFromField(Point p)
    {
        _saturation = Math.Clamp(p.X / FieldWidth, 0, 1);
        _value = 1 - Math.Clamp(p.Y / FieldHeight, 0, 1);
        Changed();
    }

    private void SetFromHue(Point p)
    {
        _hue = Math.Clamp(p.X / FieldWidth, 0, 1) * 359.999;
        Changed();
    }

    private void Changed()
    {
        Color = FromHsv(_hue, _saturation, _value);
        Show(fromBox: false);
        Moved?.Invoke(Color);
    }

    private void SetColor(Color c, bool fromBox)
    {
        Color = c;
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d > 0) // grey keeps the hue it had, so the field doesn't jump to red
        {
            double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            _hue = (h * 60 + 360) % 360;
        }
        _saturation = max == 0 ? 0 : d / max;
        _value = max;
        Show(fromBox);
    }

    private void Show(bool fromBox)
    {
        _current.Color = Color;
        _fieldHue.Color = FromHsv(_hue, 1, 1);
        _pureHue.Color = _fieldHue.Color;
        if (!fromBox)
        {
            _syncing = true;
            _hexBox.Text = $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}";
            _syncing = false;
        }
        PlaceThumbs();
    }

    private void PlaceThumbs()
    {
        Canvas.SetLeft(_fieldThumb, _saturation * FieldWidth - _fieldThumb.Width / 2);
        Canvas.SetTop(_fieldThumb, (1 - _value) * FieldHeight - _fieldThumb.Height / 2);
        Canvas.SetLeft(_hueThumb, _hue / 360 * FieldWidth - _hueThumb.Width / 2);
        Canvas.SetTop(_hueThumb, (HueHeight - _hueThumb.Height) / 2);
    }

    internal static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        static byte B(double t) => (byte)Math.Round(Math.Clamp(t, 0, 1) * 255);
        return Color.FromRgb(B(r + m), B(g + m), B(b + m));
    }
}
