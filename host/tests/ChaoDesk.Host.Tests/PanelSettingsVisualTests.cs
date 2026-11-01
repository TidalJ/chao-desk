using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml.Linq;
using ChaoDesk.Host.Localization;
using ChaoDesk.Host.Settings;
using DrawingPath = System.Windows.Shapes.Path;

namespace ChaoDesk.Host.Tests;

/// <summary>Focused settings snapshots; runs in its own process without showing windows or sending input.</summary>
public class PanelSettingsVisualTests
{
    [Fact]
    public async Task PanelEditorMatchesSettingsTypographyAndFitsItsIcons()
    {
        string? output = Environment.GetEnvironmentVariable("CHAODESK_SETTINGS_SNAPSHOTS");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var metrics = new StringBuilder();
            var failures = new List<string>();
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                // Load the real resources without starting App and its desktop services.
                var document = XDocument.Load(Path.Combine(RepoRoot(), "host", "src", "ChaoDesk.Host", "App.xaml"));
                XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var resources = document.Descendants(presentation + "ResourceDictionary").First();
                resources.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
                resources.SetAttributeValue(XNamespace.Xmlns + "settings", "clr-namespace:ChaoDesk.Host.Settings;assembly=ChaoDesk");
                foreach (var dictionary in resources.Descendants(presentation + "ResourceDictionary"))
                    if (dictionary.Attribute("Source") is { } source) source.Value = "/ChaoDesk;component/" + source.Value;
                app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());

                foreach (string language in new[] { "en", "zh" })
                foreach (string theme in new[] { "light", "dark" })
                {
                    Loc.Apply(language);
                    ThemeManager.Apply(theme);
                    var window = new SettingsWindow(UiSnapshots.FakeHost.Create()) { Width = 960, Height = 640 };
                    try
                    {
                        window.ShowPage("launcher");
                        UiSnapshots.Render(window, Path.Combine(output, $"reference-launcher-{language}-{theme}.png"));
                        window.ShowPage("panel");
                        var editor = (PanelActionsEditor)((ContentControl)window.FindName("PanelActionsHost")).Content;
                        Button FindButton(string id) => Descendants(editor).OfType<Button>()
                            .Single(button => AutomationProperties.GetAutomationId(button) == id);
                        void Click(string id) => FindButton(id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        void Render(string scene, bool compact = false)
                        {
                            window.Width = compact ? 760 : 960;
                            window.Height = compact ? 480 : 640;
                            string name = $"panel-{scene}-{language}-{theme}" + (compact ? "-small" : "");
                            UiSnapshots.Render(window, Path.Combine(output, name + ".png"), inspect: _ =>
                            {
                                metrics.AppendLine(name);
                                RecordTypography(window, editor, metrics);
                                try { CheckTypography(window, editor); }
                                catch (Exception error) { failures.Add(name + ": " + error.Message); }
                                try { CheckButtonIcons(editor); }
                                catch (Exception error) { failures.Add(name + ": " + error.Message); }
                            });
                        }

                        Render("manage");
                        Render("manage", compact: true);
                        Click("PanelManageAdd");
                        Render("add");
                        Render("add", compact: true);
                        Click("AddKind-script");
                        Render("script");
                        Render("script", compact: true);
                        Assert.Equal(3, Descendants(editor).OfType<TextBox>().Count());
                    }
                    finally { window.Close(); }
                }
                Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
                File.WriteAllText(Path.Combine(output, "typography-and-layout.txt"), metrics.ToString());
                done.SetResult();
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(output, "typography-and-layout.txt"), metrics.ToString());
                done.SetException(error);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task;
    }

    private static void CheckTypography(SettingsWindow window, PanelActionsEditor editor)
    {
        Assert.Equal(window.FontFamily.Source, editor.FontFamily.Source);
        foreach (Button button in Descendants(editor).OfType<Button>())
        {
            Assert.Equal(FontWeights.Normal, button.FontWeight);
            Assert.Equal(window.FontFamily.Source, button.FontFamily.Source);
        }
        foreach (TextBox field in Descendants(editor).OfType<TextBox>())
        {
            Assert.Equal(FontWeights.Normal, field.FontWeight);
            string id = AutomationProperties.GetAutomationId(field);
            if (id != "ActionTarget") Assert.Equal(window.FontFamily.Source, field.FontFamily.Source);
        }
        foreach (TextBlock label in Descendants(editor).OfType<TextBlock>()
            .Where(label => label.ToolTip is string && BindingOperations.GetBindingExpressionBase(label, TextBlock.TextProperty) != null))
        {
            Assert.Equal(FontWeights.Normal, label.FontWeight);
            Assert.Equal(14, label.FontSize);
            Assert.Equal(window.FontFamily.Source, label.FontFamily.Source);
        }
    }

    private static void CheckButtonIcons(PanelActionsEditor editor)
    {
        foreach (Button button in Descendants(editor).OfType<Button>())
        {
            string id = AutomationProperties.GetAutomationId(button);
            if (!id.StartsWith("Manage", StringComparison.Ordinal)) continue;
            var content = Assert.IsAssignableFrom<FrameworkElement>(button.Content);
            Rect viewport = content.TransformToAncestor(button).TransformBounds(new Rect(content.RenderSize));
            Assert.InRange(Math.Abs(viewport.Left + viewport.Width / 2 - button.ActualWidth / 2), 0, .75);
            Assert.InRange(Math.Abs(viewport.Top + viewport.Height / 2 - button.ActualHeight / 2), 0, .75);
            foreach (DrawingPath path in Descendants(content).Prepend(content).OfType<DrawingPath>())
            {
                var stroke = new Pen(path.Stroke, path.StrokeThickness)
                {
                    StartLineCap = path.StrokeStartLineCap, EndLineCap = path.StrokeEndLineCap,
                    LineJoin = path.StrokeLineJoin
                };
                Rect bounds = path.TransformToAncestor(button).TransformBounds(path.RenderedGeometry.GetRenderBounds(stroke));
                Assert.True(bounds.Left >= 1 && bounds.Top >= 1 && bounds.Right <= button.ActualWidth - 1 && bounds.Bottom <= button.ActualHeight - 1,
                    $"{id}: icon {bounds} must stay inside its {button.RenderSize} button.");
            }
        }
    }

    private static void RecordTypography(SettingsWindow window, PanelActionsEditor editor, StringBuilder output)
    {
        IEnumerable<DependencyObject> targets = new DependencyObject[] { window, editor }
            .Concat(Descendants(editor).Where(item => item is TextBlock or TextBox or Button or ComboBox));
        foreach (DependencyObject item in targets)
        {
            string label = item is TextBlock text ? text.Text : item is FrameworkElement element ? AutomationProperties.GetAutomationId(element) : "";
            var family = (FontFamily)item.GetValue(TextBlock.FontFamilyProperty);
            var weight = (FontWeight)item.GetValue(TextBlock.FontWeightProperty);
            output.Append(item.GetType().Name).Append(" [").Append(label.Replace("\r", "").Replace("\n", " ")).Append("] ")
                .Append(family.Source).Append(" (").Append(DependencyPropertyHelper.GetValueSource(item, TextBlock.FontFamilyProperty).BaseValueSource)
                .Append(") ").Append(weight).Append(" (").Append(DependencyPropertyHelper.GetValueSource(item, TextBlock.FontWeightProperty).BaseValueSource).Append(')');
            if (item is TextBlock block && VisualTreeHelper.GetDrawing(block) is { } drawing)
                output.Append(" glyphs: ").Append(string.Join(", ", GlyphFonts(drawing).Distinct()));
            output.AppendLine();
        }
        output.AppendLine();
    }

    private static IEnumerable<string> GlyphFonts(Drawing drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun.GlyphTypeface.FontUri.ToString();
        if (drawing is DrawingGroup group)
            foreach (Drawing child in group.Children)
            foreach (string font in GlyphFonts(child)) yield return font;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) yield return nested;
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "host", "ChaoDesk.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Cannot find the repository root.");
    }
}
