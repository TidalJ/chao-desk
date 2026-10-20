using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChaoDesk.Host.Library;
using ChaoDesk.Host.Localization;
using ChaoDesk.Host.Settings;

namespace ChaoDesk.Host.Tests;

/// <summary>
/// Renders the windows to PNG files, off screen (nothing is shown, no input is sent), so layout changes can be looked
/// at. Runs only with CHAODESK_SNAPSHOTS=&lt;folder&gt;: <c>dotnet test --filter UiSnapshots</c>.
/// </summary>
public class UiSnapshots
{
    [Fact]
    public void RenderWindows()
    {
        string? folder = Environment.GetEnvironmentVariable("CHAODESK_SNAPSHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current as App ?? new App();
                app.InitializeComponent();
                foreach (var language in new[] { "en", "zh" })
                {
                    Loc.Apply(language);
                    var host = FakeHost.Create();
                    var window = new SettingsWindow(host) { Width = 960, Height = 900 };
                    foreach (var page in new[] { "home", "launcher", "clipboard", "screenshot", "general", "widgets", "properties" })
                    {
                        window.ShowPage(page);
                        Render(window, Path.Combine(folder, $"settings-{page}-{language}.png"));
                    }
                    string clipFolder = Path.Combine(Path.GetTempPath(), $"chaodesk-snap-{Guid.NewGuid():N}");
                    var history = new Clips.ClipboardHistory(clipFolder, 50);
                    foreach (var (text, source) in new[] { ("Quarterly report — final figures attached.\nPlease review before Friday.", "Outlook"),
                                 ("https://github.com/TidalJ/chao-desk/releases", "Microsoft Edge"), ("会议改到周四下午三点，地点不变。", "WeChat") })
                        history.Add(new Clips.ClipItem { Kind = Clips.ClipKind.Text, Text = text, Source = source,
                            Hash = Clips.ClipItem.HashOf(Clips.ClipKind.Text, System.Text.Encoding.UTF8.GetBytes(text)) });
                    using var messages = new Desktop.MessageWindow();
                    var clip = new Clips.ClipboardWindow(history, new Clips.ClipboardMonitor(messages, history), true);
                    // Fill the list as Toggle would, without showing the popup.
                    typeof(Clips.ClipboardWindow).GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(clip, [false]);
                    Render(clip, Path.Combine(folder, $"clipboard-{language}.png"));
                    var picture = BitmapSource.Create(400, 200, 96, 96, PixelFormats.Bgra32, null, new byte[400 * 200 * 4], 400 * 4);
                    var ocr = new Ocr.OcrWindow(picture, "auto", false, false, _ => { }, (_, _, _) => { }) { Width = 1000, Height = 560 };
                    Render(ocr, Path.Combine(folder, $"ocr-{language}.png"));
                    try { Directory.Delete(clipFolder, true); } catch (IOException) { }
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    /// <summary>Lays the window's content out at the window's size and writes it as a PNG.</summary>
    private static void Render(Window window, string path)
    {
        Loc.Localize(window);
        var root = (FrameworkElement)window.Content;
        SettingRowLayout.ArrangeAll(root);
        double w = double.IsNaN(window.Width) ? 900 : window.Width, h = double.IsNaN(window.Height) ? 600 : window.Height;
        var frame = new System.Windows.Controls.Border { Background = window.Background, Width = w, Height = h };
        window.Content = null;
        frame.Child = root;
        frame.Resources.MergedDictionaries.Add(window.Resources);
        TextElement.SetFontFamily(frame, window.FontFamily);
        TextElement.SetFontSize(frame, window.FontSize);
        TextElement.SetForeground(frame, window.Foreground);
        frame.Language = window.Language;
        TextOptions.SetTextFormattingMode(frame, TextOptions.GetTextFormattingMode(window));
        frame.Measure(new Size(w, h));
        frame.Arrange(new Rect(0, 0, w, h));
        frame.UpdateLayout();
        // (no dispatcher pump here)
        frame.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(w * 1.25), (int)(h * 1.25), 120, 120, PixelFormats.Pbgra32);
        bitmap.Render(frame);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
        frame.Child = null;
        window.Content = root;
    }

    /// <summary>An <see cref="ISettingsHost"/> that answers with harmless defaults.</summary>
    public class FakeHost : DispatchProxy
    {
        private readonly AppSettings _settings = new AppSettings().Normalize();
        private WallpaperLibrary? _library;
        private Update.UpdateService? _updates;

        public static ISettingsHost Create() => Create<ISettingsHost, FakeHost>();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method == null) return null;
            string name = method.Name;
            if (name.StartsWith("add_") || name.StartsWith("remove_") || name.StartsWith("set_")) return null;
            if (name == "get_Settings") return _settings;
            if (name == "get_Library")
            {
                if (_library == null)
                {
                    string built = Path.Combine(RepoRoot(), "wallpapers");
                    _library = new WallpaperLibrary(built, Path.Combine(Path.GetTempPath(), "chaodesk-snap-user"), false);
                    _library.Scan();
                }
                return _library;
            }
            if (name == "get_Updates") return _updates ??= new Update.UpdateService(() => { });
            if (name == "get_CurrentWallpaperId") return "lotus-pond";
            if (name == "CurrentProperties") return _library?.Find("lotus-pond")?.Manifest.MergeProperties(null) ?? new JsonObject();
            if (name == "HotkeyText") return (string)args![0]! switch
            {
                "launcher" => _settings.LauncherHotkey, "clipboard" => _settings.ClipboardHotkey, "screenshot" => _settings.ScreenshotHotkey,
                "quickshot" => _settings.QuickScreenshotHotkey, "scrollshot" => _settings.ScrollScreenshotHotkey, "pin" => _settings.PinHotkey,
                "pinstoggle" => _settings.PinsToggleHotkey, "ocr" => _settings.OcrHotkey, _ => "",
            };
            if (name == "get_Theme") return "system";
            if (name == "get_Language") return Loc.Language;
            if (name == "get_TranslateEngine" || name == "get_TranslateTarget") return "auto";
            if (name == "get_OcrLanguage") return "auto";
            if (name == "get_WidgetLayout") return "stack";
            if (name == "get_CardDisplay") return "peek";
            if (name == "get_ScreenshotFolder") return @"C:\Users\me\Pictures\Chao Desk";
            if (name == "get_ClipboardIgnoredApps") return string.Join(", ", AppSettings.DefaultIgnoredApps);
            if (name == "get_ClipboardMaxItems") return 500;
            if (name == "get_Widgets") return WidgetCatalog.Catalog.Select(c => new WidgetItem { Id = c.Id, Enabled = true }).ToList();
            if (name == "get_FileSearchStatus") return Loc.F("{0:N0} files and folders indexed{1}", 1234567, "");
            if (name == "get_StatusSummary") return "";
            var type = method.ReturnType;
            if (type == typeof(void)) return null;
            if (type == typeof(string)) return "";
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type == typeof(bool)) return name is "get_ClipboardEnabled" or "get_FileSearchEnabled" or "get_QuickLookEnabled" or "get_FileSearchSkipJunk";
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                return Array.CreateInstance(type.GetGenericArguments()[0], 0);
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "host", "ChaoDesk.sln"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }
}
