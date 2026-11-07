using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ChaoDesk.Host.Clips;
using ChaoDesk.Host.Files;
using ChaoDesk.Host.Localization;

namespace ChaoDesk.Host.Settings;

/// <summary>The home page: a celadon banner with the greeting, four figures, recent copies and tasks.</summary>
public partial class SettingsWindow
{
    /// <summary>Below this width the figures go two to a row and the tasks move under the copies.</summary>
    private const double HomeWideWidth = 700;
    private bool? _homeWide;
    private bool _homeSizeHooked;
    private static ImageSource? _heroImage;
    private static string? _heroName;

    private void BuildHome()
    {
        if (!_homeSizeHooked)
        {
            _homeSizeHooked = true;
            HomePanel.SizeChanged += (_, e) =>
            {
                bool wide = e.NewSize.Width >= HomeWideWidth;
                if (_homeWide != wide && HomePage.Visibility == Visibility.Visible) BuildHome();
            };
        }
        double width = HomePanel.ActualWidth > 0 ? HomePanel.ActualWidth : (double.IsNaN(Width) ? 1280 : Width) - 310;
        bool isWide = width >= HomeWideWidth;
        _homeWide = isWide;

        var stats = _host.HomeStats ?? HomeStats.Empty;
        HomePanel.Children.Clear();
        HomePanel.Children.Add(Hero());

        var figures = new UniformGrid { Columns = isWide ? 4 : 2, Margin = new Thickness(-7, 16, -7, 0) };
        figures.Children.Add(ClipsFigure(stats));
        figures.Children.Add(AppsFigure(stats));
        figures.Children.Add(Figure(Loc.T("Screenshots this week"), stats.ScreenshotsThisWeek.ToString("N0"),
            Loc.F("{0} to capture", Hk("screenshot")), null));
        figures.Children.Add(IndexFigure(stats));
        HomePanel.Children.Add(figures);

        var clips = ClipsCard(stats);
        var tasks = TasksCard();
        if (isWide)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            clips.VerticalAlignment = VerticalAlignment.Top;
            tasks.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(clips);
            Grid.SetColumn(tasks, 2);
            row.Children.Add(tasks);
            HomePanel.Children.Add(row);
        }
        else
        {
            HomePanel.Children.Add(clips);
            tasks.Margin = new Thickness(0, 14, 0, 0);
            HomePanel.Children.Add(tasks);
        }
    }

    private string Hk(string which) => _host.HotkeyText(which) is { Length: > 0 } k ? k : Loc.T("no hotkey");

    // ---- Banner -----------------------------------------------------------------------------------------

    /// <summary>The celadon bowl with white lotus, the greeting set on its quiet left side under a veil of the page colour.</summary>
    private FrameworkElement Hero()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.GetCultureInfo(Loc.Pick("en-US", "zh-CN"));
        var corners = new CornerRadius(Math.Min(22, ThemeManager.CurrentStyle.Radius + 4));
        var photo = new Border { CornerRadius = corners };
        var veil = new Border { CornerRadius = corners };
        bool band = false;
        if (HeroImage() is { } image)
        {
            photo.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
            veil.SetResourceReference(Border.BackgroundProperty, "HeroVeilBrush");
        }
        else if (ThemeManager.CurrentStyle.Hero == "gradient")
        {
            // A banner in the accent's gradient, with light text on it.
            photo.SetResourceReference(Border.BackgroundProperty, "AccentFillBrush");
            band = true;
        }
        // A theme without a banner: the greeting stands on the page (or its backdrop) by itself.

        var texts = new StackPanel();
        texts.Children.Add(Muted(now.ToString(Loc.Pick("dddd, d MMMM", "M 月 d 日 · dddd"), culture), 13));
        texts.Children.Add(new TextBlock
        {
            Text = now.Hour < 12 ? Loc.T("Good morning") : now.Hour < 18 ? Loc.T("Good afternoon") : Loc.T("Good evening"),
            FontSize = 36, Margin = new Thickness(0, 10, 0, 0),
        });
        ((TextBlock)texts.Children[^1]).SetResourceReference(TextBlock.FontFamilyProperty, "GreetingFont");
        bool on = _host.Settings.WallpaperEnabled;
        string wallpaper = _host.Library.Find(_host.CurrentWallpaperId)?.Manifest.Name is { } n ? Loc.T(n) : Loc.T("Wallpaper");
        texts.Children.Add(Muted(on ? Loc.F("{0} is on your desktop", wallpaper) : Loc.T("The wallpaper is off."), 13.5, new Thickness(0, 6, 0, 0)));

        // The greeting sits in the middle of the banner's height; no buttons (the tools have their own hotkeys).
        bool picture = photo.Background != null || band;
        if (band)
            foreach (TextBlock line in texts.Children)
            {
                line.Foreground = Brushes.White;
                line.Opacity = line.FontSize < 20 ? 0.85 : 1;
            }
        texts.Margin = picture ? new Thickness(34, 0, 34, 0) : new Thickness(2, 0, 2, 0);
        texts.VerticalAlignment = VerticalAlignment.Center;

        var hero = new Grid { Height = picture ? 180 : 128 };
        hero.Children.Add(photo);
        hero.Children.Add(veil);
        hero.Children.Add(texts);
        return hero;
    }

    /// <summary>A picture from the app's assets, decoded at <paramref name="width"/> pixels (null name or missing: null).</summary>
    internal static ImageSource? LoadAsset(string? name, int width)
    {
        if (name == null) return null;
        using var stream = typeof(App).Assembly.GetManifestResourceStream("ChaoDesk.Host.Assets." + name);
        if (stream == null) return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = width;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>The palette's banner picture (decoded once per picture), or null when the palette has none.</summary>
    private static ImageSource? HeroImage()
    {
        string? name = ThemeManager.CurrentStyle.Hero;
        if (string.IsNullOrEmpty(name) || name == "gradient") return null;
        if (_heroImage != null && _heroName == name) return _heroImage;
        _heroName = name;
        return _heroImage = LoadAsset(name, 1600); // the banner is never wider; keeps the decoded picture small
    }

    // ---- Figures ----------------------------------------------------------------------------------------

    /// <summary>A small card: a label (with an optional chart in its corner), a big number and a line under it.</summary>
    private Border Figure(string label, string value, string note, FrameworkElement? chart, string? dotBrush = null, double valueSize = 28)
    {
        var stack = new StackPanel();
        var top = new Grid { MinHeight = 18 };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(Muted(label, 13));
        if (chart != null)
        {
            chart.VerticalAlignment = VerticalAlignment.Top;
            chart.Margin = new Thickness(8, 1, 0, -12);
            Grid.SetColumn(chart, 1);
            top.Children.Add(chart);
        }
        stack.Children.Add(top);
        stack.Children.Add(new TextBlock
        {
            Text = value, FontSize = valueSize, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI"), Margin = new Thickness(0, valueSize < 24 ? 16 : 12, 0, 0),
        });
        var noteRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        if (dotBrush != null)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Shape.FillProperty, dotBrush);
            noteRow.Children.Add(dot);
        }
        noteRow.Children.Add(Muted(note, 12.5));
        stack.Children.Add(noteRow);
        var card = HomeCardBorder(stack, new Thickness(20, 18, 20, 18));
        card.Margin = new Thickness(7, 0, 7, 14);
        return card;
    }

    private Border ClipsFigure(HomeStats stats)
    {
        if (!_host.ClipboardEnabled) return Figure(Loc.T("Copied today"), "—", Loc.T("Clipboard history is off."), null);
        int today = stats.ClipsPerDay[6], yesterday = stats.ClipsPerDay[5];
        string note = yesterday == 0 ? Loc.T("Clipboard history")
            : today == yesterday ? Loc.T("Same as yesterday")
            : Loc.F("{0:+0;-0}% vs yesterday", Math.Round((today - yesterday) * 100.0 / yesterday));
        return Figure(Loc.T("Copied today"), today.ToString("N0"), note, Bars(stats.ClipsPerDay));
    }

    private Border AppsFigure(HomeStats stats)
    {
        if (stats.TopApps.Count == 0) return Figure(Loc.T("Most opened"), Loc.T("Nothing yet"), Loc.F("{0} to open the launcher", Hk("launcher")), null, valueSize: 20);
        var (name, count) = stats.TopApps[0];
        string note = stats.TopApps.Count > 1
            ? Loc.F("Then {0}", string.Join(Loc.Pick(", ", "、"), stats.TopApps.Skip(1).Select(a => a.Name)))
            : Loc.F("Opened {0} times", count);
        var card = Figure(Loc.T("Most opened"), name, note, null, valueSize: 20);
        card.ToolTip = Loc.F("Opened {0} times", count);
        return card;
    }

    private Border IndexFigure(HomeStats stats)
    {
        if (!_host.FileSearchEnabled) return Figure(Loc.T("Indexed files"), "—", Loc.T("File search is off."), null);
        bool ready = _host.FileSearchState == FileIndexState.Ready;
        return Figure(Loc.T("Indexed files"), stats.IndexedFiles.ToString("N0"),
            ready ? Loc.T("Up to date") : _host.FileSearchStatus, null, ready ? "AccentBrush" : "PlaceholderBrush");
    }

    /// <summary>Seven thin bars, one per day; today in the accent, the rest quiet.</summary>
    private static FrameworkElement Bars(int[] perDay)
    {
        const double barWidth = 6, gap = 5, height = 28;
        int max = Math.Max(1, perDay.Max());
        var canvas = new Canvas { Width = perDay.Length * barWidth + (perDay.Length - 1) * gap, Height = height };
        for (int i = 0; i < perDay.Length; i++)
        {
            double h = Math.Max(4, height * perDay[i] / max);
            var bar = new Rectangle
            {
                Width = barWidth, Height = h, RadiusX = 3, RadiusY = 3,
                ToolTip = $"{DateTime.Today.AddDays(i - perDay.Length + 1):M/d} · {perDay[i]}",
            };
            bar.SetResourceReference(Shape.FillProperty, i == perDay.Length - 1 ? "AccentFillBrush" : "ChartRestBrush");
            Canvas.SetLeft(bar, i * (barWidth + gap));
            Canvas.SetTop(bar, height - h);
            canvas.Children.Add(bar);
        }
        return canvas;
    }

    // ---- Recent copies ----------------------------------------------------------------------------------

    private Border ClipsCard(HomeStats stats)
    {
        var stack = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var open = AccentLink(Loc.T("Open"), () => _host.OpenTool("clipboard"));
        open.ToolTip = Hk("clipboard");
        DockPanel.SetDock(open, Dock.Right);
        head.Children.Add(open);
        head.Children.Add(new TextBlock { Text = Loc.T("Clipboard"), FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(head);
        if (!_host.ClipboardEnabled || stats.RecentClips.Count == 0)
            stack.Children.Add(Muted(_host.ClipboardEnabled ? Loc.T("Nothing copied yet.") : Loc.T("Clipboard history is off."), 13, new Thickness(0, 12, 0, 8)));
        else
            for (int i = 0; i < stats.RecentClips.Count; i++) stack.Children.Add(ClipLine(stats.RecentClips[i], first: i == 0));
        return HomeCardBorder(stack, new Thickness(22, 16, 22, 8));
    }

    /// <summary>One copy: a bare icon, the text and where it came from; a hairline between rows.</summary>
    private FrameworkElement ClipLine(ClipItem clip, bool first)
    {
        string glyph = clip.Kind switch { ClipKind.Image => "", ClipKind.Files => "", _ => "" };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        grid.Children.Add(icon);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        texts.Children.Add(new TextBlock { Text = ClipboardWindow.TitleOf(clip), FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
        string sub = string.Join(" · ", new[] { clip.Source, ClipboardWindow.Ago(clip.Created) }.Where(s => s.Length > 0));
        texts.Children.Add(Muted(sub, 12, new Thickness(0, 2, 0, 0)));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        var row = new Border { Padding = new Thickness(0, 10, 0, 10), BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Child = grid };
        row.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        return row;
    }

    // ---- Tasks ------------------------------------------------------------------------------------------

    private Border TasksCard()
    {
        var tasks = _host.Tasks;
        int done = tasks.Count(t => t.Done);
        var stack = new StackPanel();
        var head = new DockPanel();
        var count = Muted(tasks.Count == 0 ? "" : Loc.F("{0} of {1} done", done, tasks.Count), 12);
        DockPanel.SetDock(count, Dock.Right);
        head.Children.Add(count);
        head.Children.Add(Muted(Loc.T("Tasks"), 13));
        stack.Children.Add(head);
        if (tasks.Count > 0)
        {
            var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 12, 0, 2) };
            track.SetResourceReference(Border.BackgroundProperty, "ChartRestBrush");
            var fill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
            fill.SetResourceReference(Border.BackgroundProperty, "AccentFillBrush");
            track.Child = fill;
            track.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * done / tasks.Count;
            stack.Children.Add(track);
        }
        var open = tasks.Where(t => !t.Done).Take(4).ToList();
        if (open.Count == 0)
            stack.Children.Add(Muted(tasks.Count == 0 ? Loc.T("No tasks.") : Loc.T("All done."), 13, new Thickness(0, 10, 0, 0)));
        foreach (var task in open)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 11, 0, 0) };
            var ring = new Ellipse { Width = 14, Height = 14, StrokeThickness = 1.5, VerticalAlignment = VerticalAlignment.Center };
            ring.SetResourceReference(Shape.StrokeProperty, "PlaceholderBrush");
            line.Children.Add(ring);
            line.Children.Add(new TextBlock { Text = task.Text, FontSize = 13.5, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(line);
        }
        var all = AccentLink(Loc.T("All tasks"), () => NavTasks.IsChecked = true);
        all.HorizontalAlignment = HorizontalAlignment.Left;
        all.Margin = new Thickness(-12, 10, 0, -4);
        stack.Children.Add(all);
        return HomeCardBorder(stack, new Thickness(22, 16, 22, 14));
    }

    // ---- Small parts ------------------------------------------------------------------------------------

    /// <summary>A text link in the accent colour (Open, All tasks).</summary>
    private Button AccentLink(string text, Action run)
    {
        var link = new Button { Content = text, Style = (Style)FindResource("TextButton"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, -12, 0) };
        link.SetResourceReference(ForegroundProperty, "AccentBrush");
        link.Click += (_, _) => run();
        return link;
    }

    private Border HomeCardBorder(UIElement child, Thickness padding) =>
        new() { Style = (Style)FindResource("Card"), Padding = padding, Child = child };

    private static TextBlock Muted(string text, double size, Thickness? margin = null, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = size, Margin = margin ?? new Thickness(0),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return block;
    }
}
