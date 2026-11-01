using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChaoDesk.Host.Panel;

namespace ChaoDesk.Host.Tests;

public class GlassSceneTests
{
    [Fact]
    public void MotionPreservesPixelDensityAndTheLensDomainAcrossDpiScales()
    {
        RunSta(() =>
        {
            // Register WPF's application-resource package without creating an
            // Application singleton or depending on another test's init order.
            using var shaderResource = Application.GetResourceStream(
                new Uri("/ChaoDesk;component/Panel/GlassLens.ps", UriKind.Relative))!.Stream;
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                // Fractional page sizes expose accidental intermediate-texture
                // scaling that an integer 324x440 DIP panel would conceal.
                var panelSize = new Size(324.3, 440.2);
                var dpi = new DpiScale(scale, scale);
                const double displacement = 37, aberration = 8;
                double reach = displacement * .5 * (1 + aberration * .1);
                var lens = new GlassLensEffect();
                var scene = new GlassScene(lens);
                scene.Configure(panelSize, dpi, 44.64, displacement, aberration);
                int width = (int)Math.Ceiling(panelSize.Width * scale) + 400;
                int height = (int)Math.Ceiling(panelSize.Height * scale) + 400;
                var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32,
                    null, new byte[width * height * 4], width * 4);
                bitmap.Freeze();
                var capture = new Rect(-2100, -750, width, height);
                var pane = new Rect(capture.Left + 200, capture.Top + 200,
                    panelSize.Width * scale, panelSize.Height * scale);
                scene.SetFrame(bitmap, capture, pane);
                scene.Measure(panelSize);
                scene.Arrange(new Rect(panelSize));
                scene.UpdateLayout();

                Rect domain = scene.LensBounds;
                var mapping = lens.Capture;
                var image = Assert.Single(Descendants(scene).OfType<Image>());
                Assert.Equal(width, image.Width * scale, 9);
                Assert.Equal(height, image.Height * scale, 9);
                foreach (double edge in new[] { domain.Left, domain.Top, domain.Right, domain.Bottom })
                    Assert.Equal(Math.Round(edge * scale), edge * scale, 9);
                Assert.True(-domain.Left * scale >= reach * scale + 2);
                Assert.True(-domain.Top * scale >= reach * scale + 2);
                Assert.True((domain.Right - panelSize.Width) * scale >= reach * scale + 2);
                Assert.True((domain.Bottom - panelSize.Height) * scale >= reach * scale + 2);

                Point firstOffset = scene.SourceOffset;
                for (int step = 1; step <= 48; step++)
                {
                    var moved = pane;
                    moved.Offset(step, -step);
                    scene.UpdateMapping(moved);
                    Assert.Same(bitmap, scene.Source);
                    Assert.Equal(domain, scene.LensBounds);
                    Assert.Equal(mapping, lens.Capture);
                    Assert.Equal(firstOffset.X - step / scale, scene.SourceOffset.X, 9);
                    Assert.Equal(firstOffset.Y + step / scale, scene.SourceOffset.Y, 9);
                    Assert.Equal(moved.Left, capture.Left + scene.RawCaptureMapping.X * capture.Width, 9);
                    Assert.Equal(moved.Top, capture.Top + scene.RawCaptureMapping.Y * capture.Height, 9);
                    foreach (var element in Descendants(scene).Prepend(scene).OfType<UIElement>())
                    {
                        Assert.True(element.IsMeasureValid, "A one-pixel move must not invalidate measure.");
                        Assert.True(element.IsArrangeValid, "A one-pixel move must not invalidate arrange.");
                    }
                }

                // The available scene must end at actual captured pixel centers
                // when the panel is flush with each virtual-desktop edge.
                scene.UpdateMapping(new Rect(capture.TopLeft, pane.Size));
                Assert.Equal((-domain.Left + .5 / scale) / domain.Width, lens.SceneBounds.X, 9);
                Assert.Equal((-domain.Top + .5 / scale) / domain.Height, lens.SceneBounds.Y, 9);
                scene.UpdateMapping(new Rect(capture.Right - pane.Width, capture.Bottom - pane.Height, pane.Width, pane.Height));
                Assert.Equal((-domain.Left + panelSize.Width - .5 / scale) / domain.Width, lens.SceneBounds.Z, 9);
                Assert.Equal((-domain.Top + panelSize.Height - .5 / scale) / domain.Height, lens.SceneBounds.W, 9);
                Assert.Equal(mapping, lens.Capture);
                foreach (var jump in new[] { new Vector(-10000, 0), new Vector(10000, 0), new Vector(0, -10000), new Vector(0, 10000) })
                {
                    var moved = pane;
                    moved.Offset(jump);
                    scene.UpdateMapping(moved);
                    var visibleSource = Rect.Intersect(new Rect(scene.SourceOffset, new Size(image.Width, image.Height)),
                        new Rect(new Point(), domain.Size));
                    Assert.False(visibleSource.IsEmpty);
                    Assert.InRange(visibleSource.Width * scale, .999999, double.MaxValue);
                    Assert.InRange(visibleSource.Height * scale, .999999, double.MaxValue);
                    Assert.Equal(1, (jump.X != 0 ? visibleSource.Width : visibleSource.Height) * scale, 7);
                    Assert.Equal(moved.Left, capture.Left + scene.RawCaptureMapping.X * capture.Width, 7);
                    Assert.Equal(moved.Top, capture.Top + scene.RawCaptureMapping.Y * capture.Height, 7);
                    Assert.InRange(lens.SceneBounds.X, 0, 1);
                    Assert.InRange(lens.SceneBounds.Y, 0, 1);
                    Assert.InRange(lens.SceneBounds.Z, lens.SceneBounds.X, 1);
                    Assert.InRange(lens.SceneBounds.W, lens.SceneBounds.Y, 1);
                    Assert.Equal(domain, scene.LensBounds);
                    Assert.Equal(mapping, lens.Capture);
                    Assert.Same(bitmap, scene.Source);
                }
                scene.Clear();
                Assert.Null(scene.Source);
            }
        });
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

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
