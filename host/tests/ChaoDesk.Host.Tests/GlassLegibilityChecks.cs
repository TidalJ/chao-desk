using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChaoDesk.Host.Panel;
using ShapePath = System.Windows.Shapes.Path;

namespace ChaoDesk.Host.Tests;

/// <summary>Checks actual panel glyphs on a synthetic white matte, without desktop capture.</summary>
internal static class GlassLegibilityChecks
{
    internal static void Run(ActionPanelWindow panel, string output)
    {
        Directory.CreateDirectory(output);
        string path = Path.Combine(output, "panel-on-white.png");
        UiSnapshots.Render(panel, path, Brushes.White);

        var root = (FrameworkElement)panel.Content;
        var action = Descendants(root).OfType<Button>().First(button => button.MinHeight == 76);
        var icon = Descendants(action).OfType<ShapePath>().Single();
        var label = Descendants(action).OfType<TextBlock>().Single();
        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(icon.Stroke).Color);
        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(label.Foreground).Color);

        var png = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(png, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        double sx = bitmap.PixelWidth / panel.Width, sy = bitmap.PixelHeight / panel.Height;

        Rect Bounds(FrameworkElement element)
        {
            var bounds = new Rect(element.TranslatePoint(new Point(), root), element.RenderSize);
            bounds.Inflate(2.5, 2.5);
            return bounds;
        }

        (int Minimum, int DarkPixels, int Pixels) Scan(Rect bounds)
        {
            int left = Math.Clamp((int)Math.Floor(bounds.Left * sx), 0, bitmap.PixelWidth);
            int right = Math.Clamp((int)Math.Ceiling(bounds.Right * sx), 0, bitmap.PixelWidth);
            int top = Math.Clamp((int)Math.Floor(bounds.Top * sy), 0, bitmap.PixelHeight);
            int bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom * sy), 0, bitmap.PixelHeight);
            int minimum = 255, dark = 0, count = 0;
            for (int y = top; y < bottom; y++) for (int x = left; x < right; x++)
            {
                int at = (y * bitmap.PixelWidth + x) * 4;
                int tone = (pixels[at] + pixels[at + 1] + pixels[at + 2]) / 3;
                minimum = Math.Min(minimum, tone);
                if (tone < 160) dark++;
                count++;
            }
            return (minimum, dark, count);
        }

        var iconStats = Scan(Bounds(icon));
        var labelStats = Scan(Bounds(label));
        // The midpoint of the six-DIP gutter is far from either action's glyphs.
        // A panel-wide grey underlay would fail this independent background check.
        var actionOrigin = action.TranslatePoint(new Point(), root);
        var gutter = Scan(new Rect(actionOrigin.X + action.ActualWidth + 2,
            actionOrigin.Y + action.ActualHeight * .5 - 1, 2, 2));
        File.WriteAllLines(Path.Combine(output, "glass-white-glyph-metrics.txt"),
        [
            "Actual first action icon and label, synthetic white matte; no desktop capture or live glass render.",
            $"Icon: minimum tone={iconStats.Minimum}/255, pixels below 160={iconStats.DarkPixels}/{iconStats.Pixels}",
            $"Label: minimum tone={labelStats.Minimum}/255, pixels below 160={labelStats.DarkPixels}/{labelStats.Pixels}",
            $"Gutter away from glyphs: minimum tone={gutter.Minimum}/255",
            "Icon stroke and label foreground remain white. This measures local dark support, not a full text contrast-ratio certification."
        ]);
        Assert.True(iconStats.DarkPixels >= 20, "White action icons need substantial local dark support on a white matte.");
        Assert.True(labelStats.DarkPixels >= 50, "White action labels need substantial local dark support on a white matte.");
        Assert.InRange(iconStats.Minimum,0,140);
        Assert.InRange(labelStats.Minimum,0,140);
        Assert.InRange(gutter.Minimum, 250, 255);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
