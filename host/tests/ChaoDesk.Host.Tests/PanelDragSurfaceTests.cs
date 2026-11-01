using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using ChaoDesk.Host.Panel;

namespace ChaoDesk.Host.Tests;

public class PanelDragSurfaceTests
{
    [Fact]
    public void OnlyConnectedNoninteractiveContentCanDragThePanel()
    {
        RunSta(() =>
        {
            // Register the resource package without creating an Application or
            // a native window. ActionPanelWindow also owns a static glyph effect.
            using var resource = Application.GetResourceStream(
                new Uri("/ChaoDesk;component/Panel/GlassGlyph.ps", UriKind.Relative))!.Stream;
            var surface = new Grid { Background = Brushes.Transparent };
            var content = new StackPanel();
            surface.Children.Add(content);
            var titleRun = new Run("Panel title");
            var title = new TextBlock(titleRun);
            var blank = new Border { Background = Brushes.Transparent, Height = 24 };
            content.Children.Add(title);
            content.Children.Add(blank);

            Control[] interactive =
            [
                new Button(), new ToggleButton(), new TextBox(), new RichTextBox(),
                new PasswordBox(), new ComboBox(), new ListBox(), new ScrollBar(), new Thumb()
            ];
            foreach (var control in interactive)
            {
                control.Height = 28;
                // An explicit nested template makes the original hit visual
                // deterministic and independent of the OS theme/default style.
                var outer = new FrameworkElementFactory(typeof(Border));
                var inner = new FrameworkElementFactory(typeof(Grid));
                var hit = new FrameworkElementFactory(typeof(TextBlock)) { Name = "HitTarget" };
                hit.SetValue(TextBlock.TextProperty, control.GetType().Name);
                inner.AppendChild(hit);
                outer.AppendChild(inner);
                control.Template = new ControlTemplate(control.GetType()) { VisualTree = outer };
                content.Children.Add(control);
            }

            var linkRun = new Run("Open a link");
            var link = new Hyperlink(linkRun);
            var linkText = new TextBlock();
            linkText.Inlines.Add(link);
            content.Children.Add(linkText);
            surface.Measure(new Size(324, 500));
            surface.Arrange(new Rect(0, 0, 324, 500));
            surface.UpdateLayout();

            Assert.True(ActionPanelWindow.IsPanelDragSurface(surface, surface));
            Assert.True(ActionPanelWindow.IsPanelDragSurface(content, surface));
            Assert.True(ActionPanelWindow.IsPanelDragSurface(title, surface));
            Assert.True(ActionPanelWindow.IsPanelDragSurface(titleRun, surface));
            Assert.True(ActionPanelWindow.IsPanelDragSurface(blank, surface));
            foreach (var control in interactive)
            {
                Assert.False(ActionPanelWindow.IsPanelDragSurface(control, surface));
                var hit = Assert.IsType<TextBlock>(control.Template.FindName("HitTarget", control));
                Assert.NotNull(VisualTreeHelper.GetParent(hit));
                Assert.True(hit.ActualWidth > 0);
                Assert.False(ActionPanelWindow.IsPanelDragSurface(hit, surface));
            }
            Assert.False(ActionPanelWindow.IsPanelDragSurface(link, surface));
            Assert.False(ActionPanelWindow.IsPanelDragSurface(linkRun, surface));

            var detachedText = new TextBlock(new Run("Unrelated content"));
            var otherSurface = new Border { Child = detachedText };
            otherSurface.Measure(new Size(100, 30));
            otherSurface.Arrange(new Rect(0, 0, 100, 30));
            Assert.False(ActionPanelWindow.IsPanelDragSurface(detachedText, surface));
            Assert.False(ActionPanelWindow.IsPanelDragSurface(otherSurface, surface));
            Assert.False(ActionPanelWindow.IsPanelDragSurface(null, surface));
        });
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
