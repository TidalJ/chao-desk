using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ChaoDesk.Host.Settings;

/// <summary>
/// Paints a theme's background — the page colour or gradient, a blurred picture under a veil, soft glows and grain —
/// for the settings window, the action panel and the little previews of themes.
/// Directions are the same everywhere: a gradient angle reads like CSS (0° up, 90° right, 135° from the top left
/// to the bottom right), and glows sit at the corners in order: top left, top right, bottom right, bottom left.
/// </summary>
internal static class ThemeBackdrop
{
    public const double DefaultAngle = 135;

    private static readonly Point[] GlowSpots = [new(0.08, 0.12), new(0.92, 0.06), new(0.9, 0.94), new(0.1, 0.92)];

    /// <summary>A brush for a gradient at a CSS-style angle, colours spaced evenly.</summary>
    public static LinearGradientBrush Linear(IReadOnlyList<Color> colours, double angle)
    {
        double rad = angle * Math.PI / 180;
        double dx = Math.Sin(rad) / 2, dy = -Math.Cos(rad) / 2;
        var brush = new LinearGradientBrush { StartPoint = new Point(0.5 - dx, 0.5 - dy), EndPoint = new Point(0.5 + dx, 0.5 + dy) };
        for (int i = 0; i < colours.Count; i++) brush.GradientStops.Add(new GradientStop(colours[i], colours.Count == 1 ? 0 : (double)i / (colours.Count - 1)));
        return brush;
    }

    public static IEnumerable<UIElement> Layers(ThemeManager.Look look, bool mini)
    {
        var s = look.Shades;
        var b = look.Version.Backdrop;
        bool dark = look.Dark;
        if (b.Kind == "linear" && b.Colours.Length > 0)
            yield return new Rectangle { Fill = Linear(b.Colours.Select(ThemeManager.Hex).ToList(), b.Angle) };
        else yield return new Rectangle { Fill = new SolidColorBrush(s.Page) };
        if (look.Picture != null && b.Kind is "wallpaper" or "image")
        {
            var image = new Image { Source = look.Picture, Stretch = Stretch.UniformToFill, Margin = new Thickness(mini ? -6 : -60),
                Effect = new BlurEffect { Radius = mini ? 4 : 40, RenderingBias = RenderingBias.Performance } };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            yield return image;
            yield return new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(dark ? (byte)0xA8 : (byte)0x8C, s.Page.R, s.Page.G, s.Page.B)) };
        }
        if (b.Kind == "glow")
            for (int i = 0; i < b.Colours.Length; i++)
            {
                var c = ThemeManager.Hex(b.Colours[i]);
                var spot = GlowSpots[i % GlowSpots.Length];
                double op = dark ? 0.55 : 0.95;
                var brush = new RadialGradientBrush { Center = spot, GradientOrigin = spot, RadiusX = 0.6, RadiusY = 0.7 };
                brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * op), c.R, c.G, c.B), 0));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(140 * op), c.R, c.G, c.B), 0.45));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
                yield return new Rectangle { Fill = brush };
            }
        if (look.Style.Grain > 0 && !mini)
            yield return new Rectangle
            {
                Fill = new ImageBrush(Noise(dark)) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 128, 128), ViewportUnits = BrushMappingMode.Absolute },
                Opacity = Math.Min(1, look.Style.Grain * 0.8),
            };
    }

    private static BitmapSource? _noiseDark, _noiseLight;

    /// <summary>A tile of random specks (white on dark themes, black on light ones) for a paper-like grain.</summary>
    private static BitmapSource Noise(bool dark)
    {
        ref var cached = ref dark ? ref _noiseDark : ref _noiseLight;
        if (cached != null) return cached;
        const int size = 128;
        var pixels = new byte[size * size * 4];
        var random = new Random(7);
        byte v = dark ? (byte)255 : (byte)0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
            pixels[i + 3] = (byte)random.Next(256);
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return cached = bitmap;
    }
}
