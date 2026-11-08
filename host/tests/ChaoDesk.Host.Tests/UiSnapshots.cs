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
                    var window = new SettingsWindow(host) { Width = 1280, Height = 900 };
                    foreach (var page in new[] { "home", "launcher", "panel", "clipboard", "screenshot", "general", "widgets", "properties" })
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
                    // A translator (never called) so the search bar shows its translate button.
                    var clip = new Clips.ClipboardWindow(history, new Clips.ClipboardMonitor(messages, history), true)
                    {
                        Translate = (_, _) => Task.FromException<ChaoDesk.Host.Translate.Translation>(new NotSupportedException()),
                    };
                    // Fill the list as Toggle would, without showing the popup.
                    typeof(Clips.ClipboardWindow).GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(clip, [false]);
                    Render(clip, Path.Combine(folder, $"clipboard-{language}.png"));
                    var narrow = new Clips.ClipboardWindow(history, new Clips.ClipboardMonitor(messages, history), false);
                    typeof(Clips.ClipboardWindow).GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(narrow, [false]);
                    Render(narrow, Path.Combine(folder, $"clipboard-narrow-{language}.png"));
                    // The action panel in the theme's own look (glass needs the live desktop, so it is not rendered here).
                    var themedPanel = new ChaoDesk.Host.Panel.ActionPanelWindow(history, new Clips.ClipboardMonitor(messages, history), () => true, _ => { },
                        (_, _) => Task.FromException<ChaoDesk.Host.Translate.Translation>(new NotSupportedException()), () => "theme");
                    Render(themedPanel, Path.Combine(folder, $"panel-theme-{language}.png"));
                    Render(SampleLauncher(), Path.Combine(folder, $"launcher-{language}.png"));
                    var picture = BitmapSource.Create(400, 200, 96, 96, PixelFormats.Bgra32, null, new byte[400 * 200 * 4], 400 * 4);
                    var ocr = new Ocr.OcrWindow(picture, "auto", false, false, _ => { }, (_, _, _) => { }) { Width = 1000, Height = 560 };
                    Render(ocr, Path.Combine(folder, $"ocr-{language}.png"));
                    try { Directory.Delete(clipFolder, true); } catch (IOException) { }
                }
                // Every palette, light and dark: the home page and the launcher.
                foreach (var palette in ThemeManager.Palettes)
                    foreach (var mode in new[] { "light", "dark" })
                    {
                        ThemeManager.SetTheme(palette.Id);
                        ThemeManager.Apply(mode);
                        var themed = new SettingsWindow(FakeHost.Create()) { Width = 1280, Height = 900 };
                        themed.ShowPage("home");
                        Render(themed, Path.Combine(folder, $"theme-{palette.Id}-{mode}-home.png"));
                        Render(SampleLauncher(), Path.Combine(folder, $"theme-{palette.Id}-{mode}-launcher.png"));
                    }
                ThemeManager.SetTheme("celadon");
                ThemeManager.Apply("light");
                // The appearance page: every tab, on an adjusted theme (with restore and save), light and dark.
                ThemeManager.SetTheme("aurora");
                ThemeManager.SetAdjust(new ThemeManager.Adjust(Accent: "#FF8A65", AccentEnd: "#E5577E", Radius: 12, Panel: "theme", PanelTiles: "glass", PanelIcons: "accent", PanelBackground: "gradient", PanelColours: ["#FFE1D2", "#E9E3FA"], PanelFont: "serif"));
                var tabField = typeof(SettingsWindow).GetField("_appearanceTab", BindingFlags.NonPublic | BindingFlags.Instance)!;
                foreach (var mode in new[] { "light", "dark" })
                {
                    ThemeManager.Apply(mode);
                    for (int tab = 0; tab < 6; tab++)
                    {
                        var page = new SettingsWindow(FakeHost.Create()) { Width = 1280, Height = 1300 };
                        tabField.SetValue(page, tab);
                        page.ShowPage("appearance");
                        Render(page, Path.Combine(folder, $"appearance-{mode}-tab{tab}.png"));
                        if (mode == "dark") break;
                    }
                }
                ThemeManager.SetAdjust(ThemeManager.Adjust.None);
                // Adjustments that change the shape of the window: a dark panel sidebar with bars, an icons-only one, a picture-free gradient.
                ThemeManager.Apply("light");
                foreach (var (name, adjust) in new[]
                         {
                             ("contrast-bar", new ThemeManager.Adjust(Sidebar: "contrast", Nav: "bar", Card: "raised")),
                             ("icons-filled", new ThemeManager.Adjust(Sidebar: "icons", Nav: "filled", Radius: 4, Font: "mono")),
                             ("float-linear", new ThemeManager.Adjust(Sidebar: "float", Light: new ThemeManager.Backdrop("linear", ["#FFE9DE", "#E9E6FB"], 120), Card: "glass")),
                             ("pink-gradient-filled", new ThemeManager.Adjust(Accent: "#FF3897", AccentEnd: "#B4EBFF", Light: new ThemeManager.Backdrop("linear", ["#FFFFFF", "#FFCEF0"], 168),
                                 Card: "glass", CardOpacity: 0.44, Font: "serif", Nav: "filled", Icons: "accent")),
                         })
                {
                    ThemeManager.SetTheme("celadon");
                    ThemeManager.SetAdjust(adjust);
                    var shaped = new SettingsWindow(FakeHost.Create()) { Width = 1280, Height = 900 };
                    shaped.ShowPage("home");
                    Render(shaped, Path.Combine(folder, $"adjusted-{name}-home.png"));
                    ThemeManager.SetAdjust(ThemeManager.Adjust.None);
                }
                ThemeManager.SetTheme("celadon");
                // "From wallpaper" with the summer pond: colours taken from the picture, the picture blurred behind.
                string summer = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../wallpapers/lotus-pond/assets/summer.jpg"));
                ThemeManager.WallpaperSource = () => summer;
                ThemeManager.WallpaperChanged();
                ThemeManager.SetTheme("wallpaper");
                foreach (var mode in new[] { "light", "dark" })
                {
                    ThemeManager.Apply(mode);
                    var wall = new SettingsWindow(FakeHost.Create()) { Width = 1280, Height = 900 };
                    wall.ShowPage("home");
                    Render(wall, Path.Combine(folder, $"theme-wallpaper-summer-{mode}-home.png"));
                }
                ThemeManager.WallpaperSource = null;
                ThemeManager.WallpaperChanged();
                ThemeManager.SetTheme("celadon");
                ThemeManager.Apply("light");
                // A window already open follows a theme change in place (nothing is rebuilt but the banner).
                var open = new SettingsWindow(FakeHost.Create()) { Width = 1280, Height = 900 };
                open.ShowPage("general");
                ThemeManager.SetTheme("autumn");
                ThemeManager.Apply("dark");
                Render(open, Path.Combine(folder, "theme-in-place-general.png"));
                ThemeManager.SetTheme("celadon");
                ThemeManager.Apply("light");
                Render(open, Path.Combine(folder, "theme-in-place-back.png"));
                // The colour picker, as its popup shows it.
                var picker = new ColourPicker(Color.FromRgb(0x1F, 0x6F, 0xB2), (Style)Application.Current.FindResource("FieldText"));
                var pickerCard = new System.Windows.Controls.Border { Child = picker, Padding = new Thickness(16), CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1) };
                pickerCard.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PopupBrush");
                pickerCard.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "LineBrush");
                var pickerWindow = new Window { Width = 340, Height = 320, Content = new System.Windows.Controls.Grid { Margin = new Thickness(24), Children = { pickerCard } } };
                pickerWindow.SetResourceReference(Window.BackgroundProperty, "PageBrush");
                Render(pickerWindow, Path.Combine(folder, "colour-picker.png"));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    /// <summary>The launcher with a typed query and a few sample results (no catalog, no file index needed).</summary>
    private static Window SampleLauncher()
    {
        var ctx = new ChaoDesk.Host.Launcher.LauncherContext
        {
            Catalog = new ChaoDesk.Host.Launcher.AppCatalog(), Files = new Files.FileIndexService(), Stats = new ChaoDesk.Host.Launcher.LaunchStats(), StatsChanged = () => { },
            Commands = () => [], FileSearchEnabled = () => false, SetUpFileSearch = () => Task.CompletedTask, AppIcon = null,
            Preview = _ => { }, ShowDetails = true, DetailsToggled = _ => { },
        };
        var window = new ChaoDesk.Host.Launcher.LauncherWindow(ctx);
        ((System.Windows.Controls.TextBox)window.FindName("QueryBox")).Text = "notes";
        var items = (System.Collections.ObjectModel.ObservableCollection<ChaoDesk.Host.Launcher.LauncherItem>)typeof(ChaoDesk.Host.Launcher.LauncherWindow)
            .GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        items.Clear();
        var md = ChaoDesk.Host.Launcher.ShellIcons.ForExtension(".md");
        var xlsx = ChaoDesk.Host.Launcher.ShellIcons.ForExtension(".xlsx");
        var folder = ChaoDesk.Host.Launcher.ShellIcons.ForPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var notepad = ChaoDesk.Host.Launcher.ShellIcons.ForPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"));
        items.Add(new ChaoDesk.Host.Launcher.LauncherItem { Id = "f1", Title = "notes-2026.md", Subtitle = @"D:\Study\Notes", Kind = ChaoDesk.Host.Launcher.LauncherItemKind.File, Icon = md, Hint = "4.2 KB" });
        items.Add(new ChaoDesk.Host.Launcher.LauncherItem { Id = "f2", Title = "Notebook budget.xlsx", Subtitle = @"D:\Work", Kind = ChaoDesk.Host.Launcher.LauncherItemKind.File, Icon = xlsx, Hint = "Yesterday" });
        items.Add(new ChaoDesk.Host.Launcher.LauncherItem { Id = "f3", Title = "Notes", Subtitle = @"D:\Study", Kind = ChaoDesk.Host.Launcher.LauncherItemKind.Folder, Icon = folder, Hint = "Folder" });
        items.Add(new ChaoDesk.Host.Launcher.LauncherItem { Id = "a1", Title = "Notepad", Subtitle = "", Kind = ChaoDesk.Host.Launcher.LauncherItemKind.App, Icon = notepad, Hint = "Alt+1" });
        typeof(ChaoDesk.Host.Launcher.LauncherWindow).GetMethod("AfterItemsChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        var results = (System.Windows.Controls.ListBox)window.FindName("Results");
        results.SelectedIndex = 0;
        var host = (System.Windows.Controls.Border)window.FindName("ResultsHost");
        host.BeginAnimation(FrameworkElement.HeightProperty, null);
        host.Height = 480;
        host.Visibility = Visibility.Visible;
        ((System.Windows.Controls.TextBlock)window.FindName("PreviewTitle")).Text = "notes-2026.md";
        ((System.Windows.Controls.TextBlock)window.FindName("PreviewDetails")).Text = "Markdown · 4.2 KB · today 14:32";
        window.Width = 880;
        window.Height = 68 + 480 + 50;
        return window;
    }

    /// <summary>Lays the window's content out at the window's size and writes it as a PNG.</summary>
    internal static void Render(Window window, string path, Brush? panelBackdrop = null, Action<FrameworkElement>? inspect = null)
    {
        Loc.Localize(window);
        var root = (FrameworkElement)window.Content;
        SettingRowLayout.ArrangeAll(root);
        double w = double.IsNaN(window.Width) ? 900 : window.Width, h = double.IsNaN(window.Height) ? 600 : window.Height;
        // A synthetic matte makes fixed-white glass labels inspectable in layout-only PNGs.
        // The optional white matte isolates glyph contrast from the optical material.
        // It is not a desktop capture or a rendering of the live optical material.
        var frame = new System.Windows.Controls.Border { Background = window is ChaoDesk.Host.Panel.ActionPanelWindow ? panelBackdrop ?? new SolidColorBrush(Color.FromRgb(34,42,51)) : window.Background, Width = w, Height = h };
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
        inspect?.Invoke(root);
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
        private Panel.PanelLayout? _panelActions;
        internal bool HotkeysSuspended { get; private set; }

        public static ISettingsHost Create() => Create<ISettingsHost, FakeHost>();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method == null) return null;
            string name = method.Name;
            if (name == "get_PanelActions") return _panelActions ??= new Panel.PanelLayout(_settings.PanelItems, items => _settings.PanelItems = items);
            if (name == "SuspendHotkeys") { HotkeysSuspended = (bool)args![0]!; return null; }
            if (name == "get_PanelGlass") return _settings.PanelGlass;
            if (name == "set_PanelGlass") { _settings.PanelGlass = (Panel.GlassOptions)args![0]!; return null; }
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
                "panel" => _settings.PanelHotkey,
                "launcher" => _settings.LauncherHotkey, "clipboard" => _settings.ClipboardHotkey, "screenshot" => _settings.ScreenshotHotkey,
                "quickshot" => _settings.QuickScreenshotHotkey, "scrollshot" => _settings.ScrollScreenshotHotkey, "pin" => _settings.PinHotkey,
                "pinstoggle" => _settings.PinsToggleHotkey, "ocr" => _settings.OcrHotkey, _ => "",
            };
            if (name == "get_Theme") return "system";
            if (name == "get_Palette") return ThemeManager.CurrentId;
            if (name == "get_ThemeAdjust") return ThemeManager.CurrentAdjust;
            if (name == "set_ThemeAdjust") { ThemeManager.SetAdjust((ThemeManager.Adjust)args![0]!); return null; }
            if (name == "get_PanelMouseMode") return _settings.PanelMouseTrigger;
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
            if (name == "get_FileSearchState") return Files.FileIndexState.Ready;
            if (name == "get_FileSearchExcluded") return new List<string> { @"D:\Backup", @"E:\Games\SteamLibrary", @"D:\Study\Archive" };
            if (name == "get_Tasks") return new List<TaskItem>
            {
                new() { Text = "Pick the new interface direction" }, new() { Text = "Release 1.3.0" },
                new() { Text = "Upgrade to .NET 10", Done = true }, new() { Text = "Sort the wallpaper assets", Done = true },
            };
            if (name == "get_HomeStats") return new HomeStats([64, 92, 71, 118, 85, 140, 128],
                [
                    new Clips.ClipItem { Kind = Clips.ClipKind.Text, Text = "./build.ps1 -Deploy", Source = "Windows Terminal", Created = DateTime.Now.AddMinutes(-2) },
                    new Clips.ClipItem { Kind = Clips.ClipKind.Image, ImageWidth = 1280, ImageHeight = 720, Source = "Screenshot", Created = DateTime.Now.AddMinutes(-18) },
                    new Clips.ClipItem { Kind = Clips.ClipKind.Text, Text = "https://github.com/TidalJ/chao-desk/releases", Source = "Microsoft Edge", Created = DateTime.Now.AddHours(-1) },
                    new Clips.ClipItem { Kind = Clips.ClipKind.Files, Text = @"D:\Work\Reports\report-final.pdf", Source = "Explorer", Created = DateTime.Now.AddHours(-5) },
                ], 23, 1283402, [("Notepad", 146), ("Visual Studio Code", 120), ("WeChat", 97)]);
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
