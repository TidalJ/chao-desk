using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChaoDesk.Host.Capture;

public enum AnnotationTool { None, Rectangle, Ellipse, Arrow, Pen, Marker, Text, Mosaic, Number }

/// <summary>
/// One mark on a screenshot, in physical pixels relative to the selection's top-left corner. The same model is
/// drawn on screen (scaled to the overlay's DIPs) and into the final image (1:1), so what you see is what you get.
/// </summary>
public sealed class Annotation
{
    public AnnotationTool Tool { get; init; }
    public List<Point> Points { get; } = new();
    public Color Color { get; init; }
    public double Width { get; init; }
    public string Text { get; set; } = "";

    /// <summary>For mosaics: the pixelated patch, made once from the screenshot.</summary>
    public BitmapSource? Patch { get; set; }

    public Rect Bounds => Points.Count == 0 ? Rect.Empty : new Rect(Points[0], Points[^1]);
}

/// <summary>Draws annotations with a DrawingContext (screen and export share this).</summary>
public static class AnnotationRenderer
{
    public static readonly Color[] Palette =
    [
        Color.FromRgb(0xE5, 0x39, 0x35), Color.FromRgb(0xFB, 0x8C, 0x00), Color.FromRgb(0xFD, 0xD8, 0x35),
        Color.FromRgb(0x43, 0xA0, 0x47), Color.FromRgb(0x1E, 0x88, 0xE5), Color.FromRgb(0x8E, 0x24, 0xAA),
        Colors.Black, Colors.White,
    ];

    public static readonly double[] Widths = [2, 4, 8];

    public static void Draw(DrawingContext dc, Annotation a, int number = 0)
    {
        var brush = new SolidColorBrush(a.Color);
        brush.Freeze();
        var pen = new Pen(brush, a.Width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        switch (a.Tool)
        {
            case AnnotationTool.Rectangle when a.Points.Count >= 2:
                dc.DrawRectangle(null, pen, a.Bounds);
                break;
            case AnnotationTool.Ellipse when a.Points.Count >= 2:
                var r = a.Bounds;
                dc.DrawEllipse(null, pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                break;
            case AnnotationTool.Arrow when a.Points.Count >= 2:
                DrawArrow(dc, brush, a.Points[0], a.Points[^1], a.Width);
                break;
            case AnnotationTool.Pen or AnnotationTool.Marker when a.Points.Count >= 2:
                var stroke = pen;
                if (a.Tool == AnnotationTool.Marker)
                {
                    var marker = new SolidColorBrush(Color.FromArgb(0x60, a.Color.R, a.Color.G, a.Color.B));
                    marker.Freeze();
                    stroke = new Pen(marker, a.Width * 4) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Round };
                    stroke.Freeze();
                }
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(a.Points[0], false, false);
                    g.PolyLineTo(a.Points.Skip(1).ToList(), true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(null, stroke, geo);
                break;
            case AnnotationTool.Text when a.Points.Count >= 1 && a.Text.Length > 0:
                dc.DrawText(TextOf(a.Text, a.Width, brush), a.Points[0]);
                break;
            case AnnotationTool.Mosaic when a.Patch != null && a.Points.Count >= 2:
                var group = new DrawingGroup();
                RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
                group.Children.Add(new ImageDrawing(a.Patch, a.Bounds));
                dc.DrawDrawing(group);
                break;
            case AnnotationTool.Number when a.Points.Count >= 1:
                double radius = 8 + a.Width * 2;
                dc.DrawEllipse(brush, new Pen(Brushes.White, Math.Max(1.5, a.Width / 2)), a.Points[0], radius, radius);
                var label = TextOf(number.ToString(CultureInfo.InvariantCulture), a.Width, IsLight(a.Color) ? Brushes.Black : Brushes.White, bold: true);
                dc.DrawText(label, new Point(a.Points[0].X - label.Width / 2, a.Points[0].Y - label.Height / 2));
                break;
        }
    }

    /// <summary>Draws every annotation; numbered markers count up in order.</summary>
    public static void DrawAll(DrawingContext dc, IEnumerable<Annotation> annotations)
    {
        int n = 0;
        foreach (var a in annotations) Draw(dc, a, a.Tool == AnnotationTool.Number ? ++n : 0);
    }

    public static double FontSizeFor(double width) => 12 + width * 3;

    public static FormattedText TextOf(string text, double width, Brush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.SemiBold, FontStretches.Normal),
            FontSizeFor(width), brush, 1.0);

    private static bool IsLight(Color c) => c.R * 0.299 + c.G * 0.587 + c.B * 0.114 > 160;

    private static void DrawArrow(DrawingContext dc, Brush brush, Point from, Point to, double width)
    {
        var v = to - from;
        double len = v.Length;
        if (len < 1) return;
        v /= len;
        double head = Math.Min(len, 10 + width * 4);
        var n = new Vector(-v.Y, v.X);
        var baseCenter = to - v * head;
        // A shaft that widens slightly toward the head, then the head: one filled shape reads best on photos.
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(from + n * (width * 0.3), true, true);
            g.LineTo(baseCenter + n * (width * 0.6), true, true);
            g.LineTo(baseCenter + n * (head * 0.45), true, true);
            g.LineTo(to, true, true);
            g.LineTo(baseCenter - n * (head * 0.45), true, true);
            g.LineTo(baseCenter - n * (width * 0.6), true, true);
            g.LineTo(from - n * (width * 0.3), true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    /// <summary>A pixelated copy of part of the screenshot (blocks of <paramref name="block"/> physical pixels).</summary>
    public static BitmapSource Pixelate(ScreenPixels shot, Desktop.PixelRect r, int block)
    {
        r = ScreenPixels.Intersect(r, shot.Bounds);
        int bw = Math.Max(1, (r.Width + block - 1) / block), bh = Math.Max(1, (r.Height + block - 1) / block);
        var small = new byte[bw * bh * 4];
        for (int by = 0; by < bh; by++)
        for (int bx = 0; bx < bw; bx++)
        {
            long sb = 0, sg = 0, sr = 0;
            int n = 0;
            for (int y = r.Y + by * block; y < Math.Min(r.Bottom, r.Y + (by + 1) * block); y++)
            for (int x = r.X + bx * block; x < Math.Min(r.Right, r.X + (bx + 1) * block); x++)
            {
                int i = (y - shot.Bounds.Y) * shot.Stride + (x - shot.Bounds.X) * 4;
                sb += shot.Data[i]; sg += shot.Data[i + 1]; sr += shot.Data[i + 2];
                n++;
            }
            int o = (by * bw + bx) * 4;
            if (n == 0) continue;
            small[o] = (byte)(sb / n); small[o + 1] = (byte)(sg / n); small[o + 2] = (byte)(sr / n); small[o + 3] = 255;
        }
        var bmp = BitmapSource.Create(bw, bh, 96, 96, PixelFormats.Bgr32, null, small, bw * 4);
        bmp.Freeze();
        return bmp;
    }
}
