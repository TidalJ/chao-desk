using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChaoDesk.Host.Panel;

namespace ChaoDesk.Host.Tests;

public class GlassBevelProfileTests
{
    [Fact]
    public async Task ProfileIsSharedFrozenAndReadableAcrossThreads()
    {
        var brush = GlassBevelProfile.Brush;
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(brush.ImageSource);
        Assert.True(brush.IsFrozen);
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(256, bitmap.PixelWidth);
        Assert.Equal(1, bitmap.PixelHeight);
        Assert.Equal(PixelFormats.Bgra32, bitmap.Format);

        var fromWorker = await Task.Run(() =>
        {
            var shared = GlassBevelProfile.Brush;
            var pixels = ReadPixels();
            Assert.Equal((byte)255, pixels[2]);
            return shared;
        });
        Assert.Same(brush, fromWorker);
        Assert.Same(bitmap, fromWorker.ImageSource);
    }

    [Fact]
    public void RefractionAndBlendFadeWhileReflectionPeaksInsideTheBevel()
    {
        byte[] pixels = ReadPixels();
        foreach (int channel in new[] { 1, 2 })
        {
            Assert.Equal((byte)255, pixels[channel]);
            Assert.Equal((byte)0, pixels[255 * 4 + channel]);
            for (int x = 1; x < 256; x++)
                Assert.True(pixels[x * 4 + channel] <= pixels[(x - 1) * 4 + channel],
                    $"Channel {channel} increased at texel {x}.");
        }

        int peak = Enumerable.Range(0, 256).MaxBy(x => pixels[x * 4]);
        Assert.InRange(peak / 255.0, .44, .48);
        Assert.InRange(pixels[peak * 4], (byte)254, (byte)255);
        Assert.Equal((byte)0, pixels[0]);
        Assert.Equal((byte)0, pixels[255 * 4]);
        for (int x = 0; x < 256; x++)
            Assert.Equal((byte)255, pixels[x * 4 + 3]);
    }

    [Fact]
    public void QuantizedRefractionMatchesIndependentSnellAngles()
    {
        byte[] pixels = ReadPixels();
        for (int x = 0; x < 256; x++)
        {
            double expected = SnellReference(x / 255.0);
            Assert.True(double.IsFinite(expected));
            Assert.InRange(expected, 0, 1);
            Assert.InRange(Math.Abs(pixels[x * 4 + 2] / 255.0 - expected),
                0, .5 / 255 + 1e-12);
        }

        var random = new Random(72463);
        for (int sample = 0; sample < 1024; sample++)
        {
            double t = random.NextDouble();
            double position = t * 255;
            int left = (int)Math.Floor(position);
            double fraction = position - left;
            double actual = (pixels[left * 4 + 2] * (1 - fraction) +
                pixels[(left + 1) * 4 + 2] * fraction) / 255;
            double continuousReference = SnellReference(t);
            double linearReference = SnellReference(left / 255.0) * (1 - fraction) +
                SnellReference((left + 1) / 255.0) * fraction;
            // The tangent profile is steepest at the outermost edge. Separate
            // the known finite-table interpolation error from byte quantization
            // rather than claiming 1/255 accuracy for the continuous curve.
            double interpolationError = Math.Abs(linearReference - continuousReference);
            Assert.InRange(Math.Abs(actual - continuousReference),
                0, interpolationError + .5 / 255 + 1e-12);
        }
    }

    private static double SnellReference(double depth)
    {
        const double ior = 1.5;
        double incidentAngle = Math.Asin(Math.Pow(1 - depth, 2));
        double transmittedAngle = Math.Asin(Math.Sin(incidentAngle) / ior);
        double edgeAngle = Math.PI / 2 - Math.Asin(1 / ior);
        return Math.Tan(incidentAngle - transmittedAngle) / Math.Tan(edgeAngle);
    }

    private static byte[] ReadPixels()
    {
        var bitmap = (BitmapSource)GlassBevelProfile.Brush.ImageSource;
        var pixels = new byte[256 * 4];
        bitmap.CopyPixels(pixels, 256 * 4, 0);
        return pixels;
    }
}
