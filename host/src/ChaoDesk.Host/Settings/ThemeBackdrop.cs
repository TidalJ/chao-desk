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

    /// <summary>The action panel's background in the theme's look: the theme's own, its page colour, or a soft wash of the accent.</summary>
    public static IEnumerable<UIElement> PanelLayers(ThemeManager.Look look, bool mini)
    {
        var s = look.Shades;
        switch (look.Style.PanelBackground)
        {
            case "plain":
                yield return new Rectangle { Fill = new SolidColorBrush(s.Page) };
                break;
            case "accent":
                Color end = look.Version.AccentEnd ?? s.Accent;
                yield return new Rectangle { Fill = Linear([ThemeManager.Mix(s.Page, s.Accent, look.Dark ? 0.3 : 0.18), s.Page, ThemeManager.Mix(s.Page, end, look.Dark ? 0.24 : 0.14)], DefaultAngle) };
                break;
            default:
                foreach (var layer in Layers(look, mini)) yield return layer;
                break;
        }
    }

    /// <summary>
    /// How the panel's action tiles look in the theme's look: like the theme's cards, or solid, frosted, outlined or
    /// flat. Returns the fill, the edge (with its width) and a shadow for raised cards.
    /// </summary>
    public static (Brush Fill, Brush Edge, double Width, Effect? Shadow) PanelTile(ThemeManager.Look look)
    {
        var s = look.Shades;
        var st = look.Style;
        bool dark = look.Dark;
        static Brush Solid(Color c) => new SolidColorBrush(c);
        string kind = st.PanelTiles != "theme" ? st.PanelTiles : st.Card switch { "glass" => "glass", "outline" => "outline", "raised" => "raised", _ => "solid" };
        return kind switch
        {
            "glass" => (Solid(dark ? Color.FromArgb(0x1C, 255, 255, 255) : Color.FromArgb((byte)(255 * Math.Clamp(st.CardOpacity, 0.35, 0.9)), 255, 255, 255)),
                        Solid(dark ? Color.FromArgb(0x26, 255, 255, 255) : Color.FromArgb(0xE6, 255, 255, 255)), 1, null),
            "outline" => (Brushes.Transparent, Solid(ThemeManager.Mix(s.Page, s.Ink, dark ? 0.26 : 0.2)), 1, null),
            "flat" => (Solid(Color.FromArgb(dark ? (byte)0x12 : (byte)0x0A, s.Ink.R, s.Ink.G, s.Ink.B)), Brushes.Transparent, 0, null),
            "raised" => (Solid(s.Card), Brushes.Transparent, 0, new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = dark ? 0.45 : 0.1 }),
            _ => (Solid(s.Card), Solid(ThemeManager.Mix(s.Page, s.Ink, dark ? 0.14 : 0.1)), 1, null),
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
