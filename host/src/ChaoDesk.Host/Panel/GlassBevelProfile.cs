using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChaoDesk.Host.Panel;

// Edge refraction profile adapted from iyinchao/liquid-glass-studio (MIT).
// See Maps/LIQUID-GLASS-STUDIO-LICENSE.txt and THIRD-PARTY-NOTICES.md.
internal static class GlassBevelProfile
{
    internal const int Samples = 256;
    internal static readonly ImageBrush Brush = Create();

    private static ImageBrush Create()
    {
        const double ior = 1.5;
        double edgeMaximum = Math.Sqrt(ior * ior - 1);
        var pixels = new byte[Samples * 4];
        for (int x = 0; x < Samples; x++)
        {
            double t = (double)x / (Samples - 1);
            double sinIncident = (1 - t) * (1 - t);
            double sinTransmitted = sinIncident / ior;
            double cosIncident = Math.Sqrt(1 - sinIncident * sinIncident);
            double cosTransmitted = Math.Sqrt(1 - sinTransmitted * sinTransmitted);
            // tan(thetaIncident - thetaTransmitted), without inverse trig.
            // Normalization keeps the edge displacement within the existing
            // capture guard instead of adding a second displacement on top.
            double refraction = (sinIncident * cosTransmitted - cosIncident * sinTransmitted) /
                (cosIncident * cosTransmitted + sinIncident * sinTransmitted) / edgeMaximum;
            double blend = 1 - t * t * (3 - 2 * t);
            double reflectionPosition = (t - .46) / .16;
            double reflection = Math.Exp(-reflectionPosition * reflectionPosition);
            int at = x * 4;
            pixels[at] = Channel(reflection);
            pixels[at + 1] = Channel(blend);
            pixels[at + 2] = Channel(refraction);
            pixels[at + 3] = 255;
        }

        // One shared immutable lookup: neither dragging nor pointer lighting
        // recreates the bitmap, brush, or its optical calculations.
        var bitmap = BitmapSource.Create(Samples, 1, 96, 96, PixelFormats.Bgra32,
            null, pixels, Samples * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    private static byte Channel(double value) =>
        (byte)Math.Clamp(Math.Round(value * 255, MidpointRounding.ToEven), 0, 255);
}
