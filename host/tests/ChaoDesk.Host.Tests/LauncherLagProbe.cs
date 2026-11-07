using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ChaoDesk.Host.Launcher;

namespace ChaoDesk.Host.Tests;

/// <summary>
/// Times the launcher's first keystroke after opening, with the real app catalog and usage counts, in a real window
/// placed off screen (no input is sent: the query is set in code). Runs only with CHAODESK_PROBE=&lt;report file&gt;.
/// </summary>
public class LauncherLagProbe
{
    [Fact]
    public void FirstKeystroke()
    {
        string? report = Environment.GetEnvironmentVariable("CHAODESK_PROBE");
        if (string.IsNullOrEmpty(report)) return;
        var lines = new List<string>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // A plain Application with App.xaml's resources: pumping messages must not run App.OnStartup (which
                // would find the installed Chao Desk running, signal it and shut this one down).
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources = AppResources();
                var catalog = new AppCatalog { CachePath = Path.Combine(AppPaths.Cache, "apps.bin") };
                var loaded = false;
                catalog.Changed += () => loaded = true;
                catalog.Refresh();
                var until = DateTime.Now.AddSeconds(15);
                while (!loaded && DateTime.Now < until) Pump(50);
                lines.Add($"apps: {catalog.Apps.Count}");
                var ctx = new LauncherContext
                {
                    Catalog = catalog, Files = new Files.FileIndexService(), Stats = LaunchStats.Load(AppPaths.LauncherStats), StatsChanged = () => { },
                    Commands = () => [], FileSearchEnabled = () => false, SetUpFileSearch = () => Task.CompletedTask, AppIcon = null,
                    Preview = _ => { }, ShowDetails = Environment.GetEnvironmentVariable("CHAODESK_PROBE_DETAILS") != "0", DetailsToggled = _ => { },
                };
                var window = new LauncherWindow(ctx) { Left = 100, Top = -30000, ShowActivated = false };
                var box = (TextBox)window.FindName("QueryBox");
                var refresh = typeof(LauncherWindow).GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!;
                for (int round = 1; round <= 3; round++)
                {
                    // As Toggle does: an empty box (recent picks), shown, then the keys.
                    box.Text = "";
                    refresh.Invoke(window, null);
                    window.Show();
                    Idle();
                    Pump(400); // let the open animation finish
                    foreach (string text in new[] { "n", "no", "not" })
                    {
                        var frames = new List<double>();
                        var frameClock = Stopwatch.StartNew();
                        double last = 0;
                        EventHandler onFrame = (_, _) => { double now = frameClock.Elapsed.TotalMilliseconds; frames.Add(now - last); last = now; };
                        CompositionTarget.Rendering += onFrame;
                        int resizes = 0;
                        SizeChangedEventHandler onResize = (_, _) => resizes++;
                        window.SizeChanged += onResize;
                        var sw = Stopwatch.StartNew();
                        box.Text = text;
                        double sync = sw.Elapsed.TotalMilliseconds;
                        Idle();
                        double idle = sw.Elapsed.TotalMilliseconds;
                        Pump(300);
                        CompositionTarget.Rendering -= onFrame;
                        window.SizeChanged -= onResize;
                        double worst = frames.Count > 1 ? frames.Skip(1).Max() : 0;
                        lines.Add($"round {round} '{text}': handler {sync:0} ms, until idle {idle:0} ms, frames {frames.Count}, worst gap {worst:0} ms, window resized {resizes}×");
                    }
                    window.Hide();
                    Pump(100);
                }
                window.Close();
                catalog.Dispose();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        File.WriteAllLines(report, lines);
        Assert.Null(failure);
    }

    /// <summary>App.xaml's resources, read as a plain dictionary (no App instance, so no OnStartup).</summary>
    private static ResourceDictionary AppResources()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "host", "ChaoDesk.sln"))) dir = dir.Parent;
        string xaml = File.ReadAllText(Path.Combine(dir!.FullName, "host", "src", "ChaoDesk.Host", "App.xaml"));
        int start = xaml.IndexOf("<ResourceDictionary>", StringComparison.Ordinal);
        int end = xaml.LastIndexOf("</ResourceDictionary>", StringComparison.Ordinal) + "</ResourceDictionary>".Length;
        string body = xaml[start..end]
            .Replace("<ResourceDictionary>", "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:settings=\"clr-namespace:ChaoDesk.Host.Settings;assembly=ChaoDesk\" xmlns:launcher=\"clr-namespace:ChaoDesk.Host.Launcher;assembly=ChaoDesk\">", StringComparison.Ordinal)
            .Replace("Source=\"Themes/Light.xaml\"", "Source=\"pack://application:,,,/ChaoDesk;component/Themes/Light.xaml\"", StringComparison.Ordinal);
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(body);
    }

    private static void Idle() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
