using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ChaoDesk.Host.Localization;
using Adjust = ChaoDesk.Host.Settings.ThemeManager.Adjust;

namespace ChaoDesk.Host.Settings;

/// <summary>
/// Settings › Appearance: the theme wall (with a filter), restore / save for an adjusted theme, and five tabs of
/// adjustments (colours and background, cards and corners, text, sidebar, density). Also the window chrome that
/// follows the theme (sidebar style and width, zoom) and the backdrop behind see-through cards.
/// </summary>
public partial class SettingsWindow
{
    private bool _picking; // a colour picker or slider is live: its colours must not rebuild the controls under it
    private int _appearanceTab;
    private string _themeFilter = "all";
    private string _shownTheme = ThemeManager.CurrentId;

    /// <summary>The theme changed (in place, so most of the window just follows): refresh what is built in code.</summary>
    private void OnColoursChanged()
    {
        UpdateBackdrop();
        ApplyChrome();
        if (_picking || ThemeManager.IsPreviewing) return;
        if (AppearancePage.Visibility == Visibility.Visible) BuildAppearance();
        if (HomePage.Visibility == Visibility.Visible) BuildHome(); // the banner follows the theme
        _shownTheme = ThemeManager.CurrentId;
    }

    // ---- Chrome: sidebar and zoom ---------------------------------------------------------------------------

    private RadioButton[] NavButtons => [NavHome, NavLauncher, NavPanel, NavClipboard, NavScreenshot, NavLibrary, NavProperties, NavWidgets, NavTasks, NavAppearance, NavGeneral, NavDiagnostics];

    private void ApplyChrome()
    {
        var st = ThemeManager.CurrentStyle;
        bool icons = st.Sidebar == "icons", floating = st.Sidebar == "float";
        RootGrid.ColumnDefinitions[0].Width = new GridLength(icons ? 76 : Math.Clamp(st.SidebarWidth, 200, 300) + (floating ? 12 : 0));
        SidebarBorder.Margin = floating ? new Thickness(12, 12, 0, 12) : new Thickness(0);
        SidebarBorder.CornerRadius = floating ? new CornerRadius(Math.Min(st.Radius, 22)) : new CornerRadius(0);
        SidebarBorder.BorderThickness = floating ? new Thickness(1) : new Thickness(0, 0, 1, 0);
        SidebarDock.Margin = icons ? new Thickness(12, 22, 12, 16) : new Thickness(14, 22, 14, 16);
        LogoRow.Margin = icons ? new Thickness(4, 0, 0, 16) : new Thickness(8, 0, 0, 16);
        LogoImage.Margin = icons ? new Thickness(0) : new Thickness(0, 0, 10, 0);
        foreach (var nav in NavButtons) nav.ToolTip = icons ? nav.Content : null;
        RootGrid.LayoutTransform = Math.Abs(st.Zoom - 1) < 0.01 ? Transform.Identity : new ScaleTransform(st.Zoom, st.Zoom);
    }

    // ---- The page -------------------------------------------------------------------------------------------

    private Adjust Current => ThemeManager.CurrentAdjust;
    private void Commit(Adjust a) => _host.ThemeAdjust = a;

    private void BuildAppearance()
    {
        AppearancePanel.Children.Clear();
        var card = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(26, 22, 26, 22) };
        var stack = new StackPanel();
        card.Child = stack;

        // Title, hint and light / dark / automatic.
        var head = new DockPanel();
        var mode = Segmented([("light", Loc.T("Light")), ("dark", Loc.T("Dark")), ("system", Loc.T("Automatic"))], _host.Theme, m => _host.Theme = m);
        DockPanel.SetDock(mode, Dock.Right);
        mode.VerticalAlignment = VerticalAlignment.Top;
        head.Children.Add(mode);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = Loc.T("Theme"), Style = (Style)FindResource("SettingLabel"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = Loc.T("Pick a theme, then adjust it to taste. You can always restore it."), Style = (Style)FindResource("SettingHint") });
        head.Children.Add(titles);
        stack.Children.Add(head);

        // Filter chips.
        var chips = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var (id, label) in new[] { ("all", "All"), ("gradient", "Gradient"), ("dark", "Dark"), ("minimal", "Minimal"), ("distinctive", "Distinctive"), ("mine", "Mine") })
        {
            if (id == "mine" && ThemeManager.Users.Count == 0) continue;
            bool on = _themeFilter == id;
            var chip = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 5, 12, 6), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand,
                Child = new TextBlock { Text = Loc.T(label), FontSize = 12.5 } };
            chip.SetResourceReference(Border.BackgroundProperty, on ? "InkBrush" : "TileBrush");
            ((TextBlock)chip.Child).SetResourceReference(TextBlock.ForegroundProperty, on ? "PaperBrush" : "InkBrush");
            string chosen = id;
            chip.MouseLeftButtonUp += (_, _) => { _themeFilter = chosen; BuildAppearance(); };
            chips.Children.Add(chip);
        }
        stack.Children.Add(chips);

        // The wall.
        var wall = new WrapPanel { Margin = new Thickness(-4, 16, 0, 0) };
        bool dark = ThemeManager.IsModeDark;
        foreach (var p in ThemeManager.Palettes)
        {
            if (_themeFilter == "mine" || (_themeFilter != "all" && !p.Tags.Contains(_themeFilter))) continue;
            var adjust = ThemeManager.Adjustments.GetValueOrDefault(p.Id) ?? Adjust.None;
            wall.Children.Add(ThemeTile(ThemeManager.Resolve(p, adjust, dark), Loc.T(p.Name), p.Id, adjusted: !adjust.IsEmpty));
        }
        if (_themeFilter is "all" or "mine")
            foreach (var u in ThemeManager.Users)
                wall.Children.Add(ThemeTile(ThemeManager.Resolve(ThemeManager.FindPalette(u.Base), u.Adjust, dark), Loc.T(u.Name), u.Id, adjusted: false));
        wall.Children.Add(NewThemeTile());
        stack.Children.Add(wall);

        // What is adjusted, with restore and save.
        if (StatusBar() is { } bar) stack.Children.Add(bar);

        // Tabs.
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        string[] names = ["Colours and background", "Cards and corners", "Text", "Sidebar", "Density and motion"];
        for (int i = 0; i < names.Length; i++)
        {
            bool on = i == _appearanceTab;
            var label = new TextBlock { Text = Loc.T(names[i]), FontSize = 13.5, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal };
            label.SetResourceReference(TextBlock.ForegroundProperty, on ? "InkBrush" : "MutedBrush");
            var tab = new Border { Child = label, Padding = new Thickness(2, 10, 2, 10), Margin = new Thickness(0, 0, 26, 0), BorderThickness = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            if (on) tab.SetResourceReference(Border.BorderBrushProperty, "InkBrush"); else tab.BorderBrush = Brushes.Transparent;
            int index = i;
            tab.MouseLeftButtonUp += (_, _) => { _appearanceTab = index; BuildAppearance(); };
            tabs.Children.Add(tab);
        }
        stack.Children.Add(tabs);
        var rule = new Border { Height = 1, Margin = new Thickness(-26, 0, -26, 4) };
        rule.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        stack.Children.Add(rule);
        var rows = new Grid();
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        rows.ColumnDefinitions.Add(new ColumnDefinition());
        switch (_appearanceTab)
        {
            case 0: ColourTab(rows); break;
            case 1: CardsTab(rows); break;
            case 2: TextTab(rows); break;
            case 3: SidebarTab(rows); break;
            default: DensityTab(rows); break;
        }
        stack.Children.Add(rows);
        AppearancePanel.Children.Add(card);
    }

    private FrameworkElement? StatusBar()
    {
        var user = ThemeManager.CurrentUser;
        var adjust = Current;
        if (user == null && adjust.IsEmpty) return null;
        var bar = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var box = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 10, 10, 10), Child = bar, Margin = new Thickness(0, 14, 0, 0) };
        box.SetResourceReference(Border.BackgroundProperty, "TileBrush");
        bar.Margin = new Thickness(0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        Button Make(string text, string glyph, Action click)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Style = (Style)FindResource("SecondaryButton"), Content = content, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 6, 14, 7) };
            b.Click += (_, _) => click();
            return b;
        }
        string name;
        string detail;
        if (user != null)
        {
            name = Loc.T(user.Name);
            detail = Loc.F("Your theme, based on {0}", Loc.T(ThemeManager.FindPalette(user.Base).Name));
            buttons.Children.Add(Make(Loc.T("Delete theme"), "", () => _host.DeleteTheme(user.Id)));
        }
        else
        {
            name = Loc.T(ThemeManager.Current.Name);
            detail = Loc.F("Adjusted: {0}", string.Join(Loc.Pick(", ", "、"), adjust.Changes().Select(c => Loc.T(c))));
            buttons.Children.Add(Make(Loc.T("Restore theme"), "", () => Commit(Adjust.None)));
            buttons.Children.Add(Make(Loc.T("Save as my theme"), "", () => _host.SaveTheme()));
        }
        bar.Children.Add(buttons);
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(new System.Windows.Documents.Run(name) { FontWeight = FontWeights.SemiBold });
        var rest = new System.Windows.Documents.Run("   " + detail);
        rest.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "MutedBrush");
        text.Inlines.Add(rest);
        bar.Children.Add(text);
        return box;
    }

    // ---- Tiles ----------------------------------------------------------------------------------------------

    private FrameworkElement ThemeTile(ThemeManager.Look look, string name, string id, bool adjusted)
    {
        const double W = 150, H = 96;
        bool selected = id == ThemeManager.CurrentId;
        var face = new Grid { Width = W, Height = H, Clip = new RectangleGeometry(new Rect(0, 0, W, H), 12, 12) };
        face.Children.Add(MiniLook(look, W, H));
        var edge = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), IsHitTestVisible = false };
        edge.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        face.Children.Add(edge);
        if (selected)
        {
            var tick = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 7, 0), Background = Brushes.White,
                Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            face.Children.Add(tick);
        }
        var frame = new Border { CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(2), Padding = new Thickness(2), Child = face, Background = Brushes.Transparent };
        if (selected) frame.SetResourceReference(Border.BorderBrushProperty, "InkBrush"); else frame.BorderBrush = Brushes.Transparent;

        var label = new DockPanel { Margin = new Thickness(5, 7, 3, 0) };
        if (adjusted)
        {
            var badge = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 2), Background = new SolidColorBrush(Color.FromArgb(0x33, 0xE0, 0x9A, 0x2B)),
                Child = new TextBlock { Text = Loc.T("Adjusted"), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x74, 0x10)) } };
            DockPanel.SetDock(badge, Dock.Right);
            label.Children.Add(badge);
        }
        var text = new TextBlock { Text = name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, selected ? "InkBrush" : "MutedBrush");
        if (selected) text.FontWeight = FontWeights.SemiBold;
        label.Children.Add(text);

        var tile = new StackPanel { Width = W + 8, Margin = new Thickness(4, 0, 8, 14), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        tile.Children.Add(frame);
        tile.Children.Add(label);
        tile.MouseLeftButtonUp += (_, _) => { if (id != ThemeManager.CurrentId) _host.Palette = id; };
        return tile;
    }

    private FrameworkElement NewThemeTile()
    {
        var dash = new Rectangle { RadiusX = 15, RadiusY = 15, StrokeThickness = 1.5, StrokeDashArray = [4, 3] };
        dash.SetResourceReference(Shape.StrokeProperty, "ToggleTrackBrush");
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var plus = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center };
        var label = new TextBlock { Text = Loc.T("New theme"), FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        plus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        content.Children.Add(plus);
        content.Children.Add(label);
        var grid = new Grid { Width = 154, Height = 100, Margin = new Thickness(4, 0, 8, 14), VerticalAlignment = VerticalAlignment.Top, Cursor = Cursors.Hand, Background = Brushes.Transparent,
            ToolTip = Loc.T("Save the current theme, with its adjustments, as your own") };
        grid.Children.Add(dash);
        grid.Children.Add(content);
        grid.MouseLeftButtonUp += (_, _) => _host.SaveTheme();
        return grid;
    }

    /// <summary>A tiny picture of the app in a look: its backdrop, sidebar style, title font, cards, accent and corners.</summary>
    private static FrameworkElement MiniLook(ThemeManager.Look look, double w, double h)
    {
        var s = look.Shades;
        var st = look.Style;
        var root = new Grid { Width = w, Height = h, ClipToBounds = true };
        foreach (var layer in BackdropLayers(look, mini: true)) root.Children.Add(layer);
        Brush Solid(Color c) => new SolidColorBrush(c);
        Brush accent = look.Version.AccentEnd is { } end ? new LinearGradientBrush(s.Accent, end, 45) : Solid(s.Accent);
        byte alpha = st.Card switch { "glass" => (byte)(255 * st.CardOpacity), "outline" => (byte)0, _ => (byte)255 };
        Brush surface = Solid(Color.FromArgb(alpha, s.Card.R, s.Card.G, s.Card.B));
        Brush line = Solid(ThemeManager.Mix(s.Page, s.Ink, st.Card == "outline" ? 0.2 : 0.1));
        double r = Math.Min(st.Radius / 2.5, 8);
        double sideW = st.Sidebar == "icons" ? 12 : 26;

        if (st.Sidebar == "contrast")
            root.Children.Add(new Border { Width = sideW, HorizontalAlignment = HorizontalAlignment.Left, Background = Solid(look.Version.Side ?? ThemeManager.Mix(ThemeManager.Hex("#1C1F26"), s.Accent, 0.1)) });
        else if (st.Sidebar == "float")
            root.Children.Add(new Border { Width = sideW - 6, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4), CornerRadius = new CornerRadius(4),
                Background = Solid(look.Dark ? Color.FromArgb(0x26, 255, 255, 255) : Color.FromArgb(0xC0, 255, 255, 255)) });
        else
            root.Children.Add(new Border { Width = sideW, HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = Solid(ThemeManager.Mix(s.Page, s.Ink, 0.08)),
                Background = st.Sidebar == "icons" ? Solid(ThemeManager.Mix(s.Page, s.Card, 0.5)) : Brushes.Transparent });

        var main = new StackPanel { Margin = new Thickness(sideW + 9, 9, 9, 0) };
        bool band = st.Hero == "gradient";
        var aa = new TextBlock { Text = "Aa", FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Solid(band ? Colors.White : s.Ink),
            FontFamily = new FontFamily(st.Font switch { "serif" => "Georgia, SimSun", "geo" => "Bahnschrift", "mono" => "Cascadia Mono, Consolas", _ => "Segoe UI Variable Display, Segoe UI" }) };
        if (band) root.Children.Add(new Border { Height = h * 0.42, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(sideW, 0, 0, 0), Background = accent });
        else if (st.Hero is { Length: > 0 } hero && LoadAsset(hero, 160) is { } picture)
        {
            main.Children.Add(new Border { Height = h * 0.26, CornerRadius = new CornerRadius(r), Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill }, Margin = new Thickness(0, 0, 0, 5) });
            aa = null;
        }
        if (aa != null) main.Children.Add(aa);
        for (int i = 0; i < 2; i++)
        {
            var row = new Grid { Height = h * 0.2, Margin = new Thickness(0, i == 0 ? 7 : 5, 0, 0) };
            var cardFace = new Border { CornerRadius = new CornerRadius(r), Background = surface, BorderBrush = line, BorderThickness = new Thickness(st.Card == "raised" ? 0 : 0.8) };
            if (st.Card == "raised") cardFace.Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1.5, Opacity = look.Dark ? 0.4 : 0.12, Direction = 270 };
            row.Children.Add(cardFace);
            if (i == 0) row.Children.Add(new Border { Width = 18, Height = 8, CornerRadius = new CornerRadius(r < 2 ? 1 : 4), Background = accent, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0) });
            main.Children.Add(row);
        }
        root.Children.Add(main);
        return root;
    }

    // ---- Tab: colours and background ------------------------------------------------------------------------

    private void ColourTab(Grid rows)
    {
        var look = ThemeManager.CurrentLook;
        bool modeDark = ThemeManager.IsModeDark;
        var backdrop = look.Version.Backdrop;
        Adjust WithBackdrop(Adjust a, ThemeManager.Backdrop b) => modeDark ? a with { Dark = b } : a with { Light = b };
        string page = ThemeManager.Hex(look.Shades.Page);
        string[] colours = backdrop.Colours.Length > 0 ? backdrop.Colours : [page];

        var kind = Segmented([("solid", Loc.T("Plain")), ("linear", Loc.T("Gradient")), ("glow", Loc.T("Glow")), ("wallpaper", Loc.T("Wallpaper")), ("image", Loc.T("Picture"))], backdrop.Kind, k =>
        {
            string[] two = colours.Length >= 2 ? colours : [page, ThemeManager.Hex(ThemeManager.Mix(look.Shades.Page, look.Shades.Accent, 0.28))];
            ThemeManager.Backdrop? next = k switch
            {
                "solid" => new("solid", [colours[0]]),
                "linear" => new("linear", two, backdrop.Angle),
                "glow" => new("glow", two),
                "wallpaper" => new("wallpaper", []),
                _ => PickImage() is { } file ? new ThemeManager.Backdrop("image", [], 135, file) : null,
            };
            if (next != null) Commit(WithBackdrop(Current, next));
            else BuildAppearance();
        });
        AddRow(rows, Loc.T("Background"), kind);

        if (backdrop.Kind is "solid" or "linear" or "glow")
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (backdrop.Kind == "solid")
                BuildColourRow(line, ThemeManager.Backgrounds, colours[0],
                    hex => Commit(WithBackdrop(Current, new("solid", [hex]))), hex => ThemeManager.Preview(WithBackdrop(Current, new("solid", [hex]))));
            else
            {
                var strip = new Border { Width = 220, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 12, 0), Background = GradientOf(colours, 0) };
                line.Children.Add(strip);
                for (int i = 0; i < colours.Length; i++)
                {
                    int index = i;
                    ThemeManager.Backdrop With(string hex) { var c = colours.ToArray(); c[index] = hex; return backdrop with { Colours = c }; }
                    Border swatch = null!;
                    swatch = Swatch(colours[i], Loc.T("Click to change this colour"), false, () => OpenPicker(swatch, colours[index],
                        hex => Commit(WithBackdrop(Current, With(hex))), hex => ThemeManager.Preview(WithBackdrop(Current, With(hex)))));
                    if (colours.Length > 2)
                        swatch.MouseRightButtonUp += (_, _) => Commit(WithBackdrop(Current, backdrop with { Colours = colours.Where((_, j) => j != index).ToArray() }));
                    line.Children.Add(swatch);
                }
                if (colours.Length < 4)
                {
                    var add = new Button { Style = (Style)FindResource("SecondaryButton"), Content = Loc.T("+ Colour"), Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(12, 4, 12, 5),
                        ToolTip = Loc.T("Right-click a colour to remove it") };
                    add.Click += (_, _) => Commit(WithBackdrop(Current, backdrop with { Colours = [.. colours, ThemeManager.Hex(ThemeManager.Mix(ThemeManager.Hex(colours[^1]), look.Shades.Accent, 0.3))] }));
                    line.Children.Add(add);
                }
            }
            AddRow(rows, backdrop.Kind == "solid" ? Loc.T("Page colour") : Loc.T("Colours"), line);
            if (backdrop.Kind == "linear")
                AddRow(rows, Loc.T("Direction"), SliderRow(0, 360, backdrop.Angle, "0°", "360°",
                    v => WithBackdrop(Current, backdrop with { Angle = Math.Round(v) })));
        }
        else if (backdrop.Kind == "image")
        {
            var change = new Button { Style = (Style)FindResource("SecondaryButton"), Content = Loc.T("Choose another picture…"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 5, 14, 6) };
            change.Click += (_, _) => { if (PickImage() is { } file) Commit(WithBackdrop(Current, new("image", [], 135, file))); };
            AddRow(rows, Loc.T("Picture"), change);
        }

        // Accent: the theme's own, plain presets, gradient presets, any colour.
        var accents = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var a = Current;
        var own = ThemeManager.Resolve(ThemeManager.Current, Adjust.None, modeDark).Version;
        var ownSwatch = GradientSwatch(ThemeManager.Hex(own.Shades.Accent), own.AccentEnd is { } oe ? ThemeManager.Hex(oe) : null, Loc.T("The theme's own"), a.Accent == null && a.AccentEnd == null,
            () => Commit(Current with { Accent = null, AccentEnd = null }));
        accents.Children.Add(ownSwatch);
        accents.Children.Add(Divider());
        foreach (var (name, hex) in ThemeManager.Accents)
        {
            string chosen = hex;
            accents.Children.Add(Swatch(hex, Loc.T(name), string.Equals(a.Accent, hex, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(a.AccentEnd), () => Commit(Current with { Accent = chosen, AccentEnd = "" })));
        }
        accents.Children.Add(Divider());
        foreach (var (name, start, end) in ThemeManager.GradientAccents)
        {
            string s0 = start, e0 = end;
            accents.Children.Add(GradientSwatch(start, end, Loc.T(name), string.Equals(a.Accent, start, StringComparison.OrdinalIgnoreCase) && string.Equals(a.AccentEnd, end, StringComparison.OrdinalIgnoreCase),
                () => Commit(Current with { Accent = s0, AccentEnd = e0 })));
        }
        accents.Children.Add(Divider());
        string currentAccent = ThemeManager.Hex(look.Shades.Accent);
        bool custom = a.Accent != null && !ThemeManager.Accents.Any(x => x.Hex.Equals(a.Accent, StringComparison.OrdinalIgnoreCase)) && !ThemeManager.GradientAccents.Any(x => x.Start.Equals(a.Accent, StringComparison.OrdinalIgnoreCase));
        Border pick = null!;
        pick = Swatch(custom ? a.Accent! : "#FFFFFF", Loc.T("Pick any colour"), custom, () => OpenPicker(pick, a.Accent ?? currentAccent,
            hex => Commit(Current with { Accent = hex, AccentEnd = "" }), hex => ThemeManager.Preview(Current with { Accent = hex, AccentEnd = "" })));
        if (!custom && pick.Child is Ellipse spectrum) { spectrum.Fill = Spectrum(); spectrum.StrokeThickness = 0; }
        accents.Children.Add(pick);
        AddRow(rows, Loc.T("Accent"), accents);

        AddRow(rows, Loc.T("Grain"), SliderRow(0, 0.08, look.Style.Grain, Loc.T("None"), Loc.T("Strong"), v => Current with { Grain = Math.Round(v, 3) }));
        AddRow(rows, Loc.T("Home banner"), Segmented([("", Loc.T("None")), ("home-hero.jpg", Loc.T("Celadon")), ("hero-summer.jpg", Loc.T("Summer")), ("hero-autumn.jpg", Loc.T("Autumn pond")), ("gradient", Loc.T("Gradient"))],
            look.Style.Hero, h => Commit(Current with { Hero = h })));
    }

    private string? PickImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Pictures") + "|*.jpg;*.jpeg;*.png;*.bmp;*.webp", Title = Loc.T("Choose a background picture") };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    // ---- Tab: cards and corners -----------------------------------------------------------------------------

    private void CardsTab(Grid rows)
    {
        var st = ThemeManager.CurrentStyle;
        var tiles = new WrapPanel();
        foreach (var (id, name) in new[] { ("solid", "Solid"), ("glass", "Frosted glass"), ("outline", "Outline"), ("raised", "Raised") })
        {
            var demo = new Grid { Width = 92, Height = 46 };
            demo.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = new LinearGradientBrush(Color.FromRgb(0xD9, 0xEE, 0xE4), Color.FromRgb(0xDF, 0xE7, 0xF4), 30) });
            var face = new Border { Margin = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(6) };
            switch (id)
            {
                case "solid": face.Background = Brushes.White; face.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xEA, 0xE7)); face.BorderThickness = new Thickness(1); break;
                case "glass": face.Background = new SolidColorBrush(Color.FromArgb(0x8C, 255, 255, 255)); face.BorderBrush = Brushes.White; face.BorderThickness = new Thickness(1); break;
                case "outline": face.BorderBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA8, 0xA2)); face.BorderThickness = new Thickness(1); break;
                default: face.Background = Brushes.White; face.Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 3, Opacity = 0.18, Direction = 270 }; break;
            }
            demo.Children.Add(face);
            string chosen = id;
            tiles.Children.Add(OptionTile(demo, Loc.T(name), st.Card == id, () => Commit(Current with { Card = chosen })));
        }
        AddRow(rows, Loc.T("Card material"), tiles);
        AddRow(rows, Loc.T("Corners"), SliderRow(0, 28, st.Radius, Loc.T("Square"), Loc.T("Round"), v => Current with { Radius = Math.Round(v) }));
        var opacity = SliderRow(0.3, 1, st.CardOpacity, Loc.T("Clear"), Loc.T("Solid"), v => Current with { CardOpacity = Math.Round(v, 2) });
        opacity.IsEnabled = st.Card == "glass";
        opacity.ToolTip = Loc.T("For frosted glass cards");
        AddRow(rows, Loc.T("Card opacity"), opacity);
    }

    // ---- Tab: text ------------------------------------------------------------------------------------------

    private void TextTab(Grid rows)
    {
        var st = ThemeManager.CurrentStyle;
        var tiles = new WrapPanel();
        foreach (var (id, name, family) in new[] { ("sans", "Sans", "Segoe UI Variable Display, Microsoft YaHei UI"), ("serif", "Serif", "Georgia, Noto Serif SC, Source Han Serif SC, SimSun"),
                     ("geo", "Geometric", "Bahnschrift, Microsoft YaHei UI"), ("mono", "Monospace", "Cascadia Mono, Consolas, Microsoft YaHei UI") })
        {
            var sample = new TextBlock { Text = Loc.T("Aa Good"), FontFamily = new FontFamily(family), FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 4) };
            sample.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            string chosen = id;
            tiles.Children.Add(OptionTile(sample, Loc.T(name), st.Font == id, () => Commit(Current with { Font = chosen })));
        }
        AddRow(rows, Loc.T("Title font"), tiles);
        AddRow(rows, Loc.T("Body font"), Segmented([("system", Loc.T("System")), ("title", Loc.T("Same as titles")), ("mono", Loc.T("Monospace"))], st.Body, b => Commit(Current with { Body = b })));
        AddRow(rows, Loc.T("Size"), SliderRow(0.9, 1.2, st.Zoom, "A", "A", v => Current with { Zoom = Math.Round(v, 2) }, big: true));
    }

    // ---- Tab: sidebar ---------------------------------------------------------------------------------------

    private void SidebarTab(Grid rows)
    {
        var st = ThemeManager.CurrentStyle;
        var styles = new WrapPanel();
        foreach (var (id, name) in new[] { ("same", "Like the page"), ("contrast", "Dark panel"), ("float", "Floating glass"), ("icons", "Icons only") })
        {
            var demo = new Grid { Width = 100, Height = 56, ClipToBounds = true };
            demo.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = new LinearGradientBrush(Color.FromRgb(0xD9, 0xEE, 0xE4), Color.FromRgb(0xDF, 0xE7, 0xF4), 30) });
            var side = new Border { HorizontalAlignment = HorizontalAlignment.Left };
            switch (id)
            {
                case "same": side.Width = 32; side.BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)); side.BorderThickness = new Thickness(0, 0, 1, 0); break;
                case "contrast": side.Width = 32; side.Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x36, 0x44)); side.CornerRadius = new CornerRadius(8, 0, 0, 8); break;
                case "float": side.Width = 28; side.Margin = new Thickness(5); side.CornerRadius = new CornerRadius(6); side.Background = new SolidColorBrush(Color.FromArgb(0xD0, 255, 255, 255)); break;
                default: side.Width = 14; side.Background = Brushes.White; side.CornerRadius = new CornerRadius(8, 0, 0, 8); break;
            }
            demo.Children.Add(side);
            string chosen = id;
            styles.Children.Add(OptionTile(demo, Loc.T(name), st.Sidebar == id, () => Commit(Current with { Sidebar = chosen })));
        }
        AddRow(rows, Loc.T("Sidebar style"), styles);

        var marks = new WrapPanel();
        foreach (var (id, name) in new[] { ("pill", "Pill"), ("bar", "Side bar"), ("filled", "Filled"), ("line", "Underline") })
        {
            var item = new Border { Width = 92, Height = 30, Padding = new Thickness(10, 0, 0, 0) };
            var text = new TextBlock { Text = Loc.T("Overview"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            item.Child = text;
            switch (id)
            {
                case "pill": item.CornerRadius = new CornerRadius(8); item.SetResourceReference(Border.BackgroundProperty, "CardBrush"); item.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); item.BorderThickness = new Thickness(1); text.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush"); break;
                case "bar": item.BorderThickness = new Thickness(3, 0, 0, 0); item.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); text.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); break;
                case "filled": item.CornerRadius = new CornerRadius(8); item.SetResourceReference(Border.BackgroundProperty, "AccentFillBrush"); text.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush"); break;
                default: item.BorderThickness = new Thickness(0, 0, 0, 2); item.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); text.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush"); break;
            }
            string chosen = id;
            marks.Children.Add(OptionTile(item, Loc.T(name), st.Nav == id, () => Commit(Current with { Nav = chosen })));
        }
        AddRow(rows, Loc.T("Current page"), marks);
        AddRow(rows, Loc.T("Icons"), Segmented([("line", Loc.T("Grey")), ("accent", Loc.T("Accent")), ("none", Loc.T("Hidden"))], st.Icons, i => Commit(Current with { Icons = i })));
        var width = SliderRow(200, 300, st.SidebarWidth, Loc.T("Narrow"), Loc.T("Wide"), v => Current with { SidebarWidth = Math.Round(v) });
        width.IsEnabled = st.Sidebar != "icons";
        AddRow(rows, Loc.T("Width"), width);
    }

    // ---- Tab: density and motion ----------------------------------------------------------------------------

    private void DensityTab(Grid rows)
    {
        var st = ThemeManager.CurrentStyle;
        AddRow(rows, Loc.T("Density"), Segmented([("compact", Loc.T("Compact")), ("default", Loc.T("Default")), ("roomy", Loc.T("Roomy"))], st.Density, d => Commit(Current with { Density = d })));
        AddRow(rows, Loc.T("Theme changes"), Segmented([("on", Loc.T("Glide")), ("off", Loc.T("Switch at once"))], st.Motion ? "on" : "off", m => Commit(Current with { Motion = m == "on" })));
    }

    // ---- Building blocks ------------------------------------------------------------------------------------

    private static void AddRow(Grid rows, string label, FrameworkElement content)
    {
        int r = rows.RowDefinitions.Count;
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 14, 12, 14) };
        Grid.SetRow(text, r);
        content.Margin = new Thickness(0, 10, 0, 10);
        if (content.VerticalAlignment == VerticalAlignment.Stretch) content.VerticalAlignment = VerticalAlignment.Center;
        if (content.HorizontalAlignment == HorizontalAlignment.Stretch) content.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(content, r);
        Grid.SetColumn(content, 1);
        rows.Children.Add(text);
        rows.Children.Add(content);
        if (r > 0)
        {
            var rule = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top };
            rule.SetResourceReference(Border.BackgroundProperty, "LineBrush");
            Grid.SetRow(rule, r);
            Grid.SetColumnSpan(rule, 2);
            rows.Children.Add(rule);
        }
    }

    private FrameworkElement Segmented((string Value, string Label)[] options, string current, Action<string> pick)
    {
        var bar = new Border { Style = (Style)FindResource("SegmentBar"), HorizontalAlignment = HorizontalAlignment.Left };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(3) };
        string group = "seg" + Guid.NewGuid().ToString("N");
        foreach (var (value, label) in options)
        {
            var tab = new RadioButton { Style = (Style)FindResource("SegmentTab"), GroupName = group, Content = label, IsChecked = value == current };
            string chosen = value;
            tab.Checked += (_, _) => { if (chosen != current) Dispatcher.BeginInvoke(() => pick(chosen)); };
            panel.Children.Add(tab);
        }
        bar.Child = panel;
        return bar;
    }

    /// <summary>A slider that shows its value live while dragged and keeps it when let go.</summary>
    private FrameworkElement SliderRow(double min, double max, double value, string left, string right, Func<double, Adjust> make, bool big = false)
    {
        var dock = new DockPanel { Width = 380 };
        var l = new TextBlock { Text = left, FontSize = big ? 11 : 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var r = new TextBlock { Text = right, FontSize = big ? 17 : 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        l.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        r.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(l, Dock.Left);
        DockPanel.SetDock(r, Dock.Right);
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
        bool moved = false;
        slider.ValueChanged += (_, e) => { if (!slider.IsLoaded) return; moved = true; _picking = true; ThemeManager.Preview(make(e.NewValue)); };
        void Done() { _picking = false; if (moved) { moved = false; var a = make(slider.Value); Dispatcher.BeginInvoke(() => Commit(a)); } }
        slider.LostMouseCapture += (_, _) => Done();
        slider.KeyUp += (_, _) => Done();
        dock.Children.Add(l);
        dock.Children.Add(r);
        dock.Children.Add(slider);
        return dock;
    }

    private static FrameworkElement OptionTile(FrameworkElement content, string label, bool selected, Action pick)
    {
        var stack = new StackPanel();
        stack.Children.Add(content);
        var text = new TextBlock { Text = label, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, selected ? "InkBrush" : "MutedBrush");
        if (selected) text.FontWeight = FontWeights.SemiBold;
        stack.Children.Add(text);
        var tile = new Border { Child = stack, Padding = new Thickness(8), Margin = new Thickness(0, 0, 10, 0), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(selected ? 2 : 1), Cursor = Cursors.Hand, MinWidth = 108 };
        tile.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        tile.SetResourceReference(Border.BorderBrushProperty, selected ? "InkBrush" : "LineBrush");
        tile.MouseLeftButtonUp += (_, _) => { if (!selected) pick(); };
        return tile;
    }

    private static Border Divider()
    {
        var gap = new Border { Width = 1, Height = 20, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        gap.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        return gap;
    }

    private static Brush GradientOf(string[] colours, double angle)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        for (int i = 0; i < colours.Length; i++) brush.GradientStops.Add(new GradientStop(ThemeManager.Hex(colours[i]), colours.Length == 1 ? 0 : (double)i / (colours.Length - 1)));
        return brush;
    }

    private static Brush Spectrum()
    {
        var hues = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        for (int i = 0; i <= 6; i++) hues.GradientStops.Add(new GradientStop(ColourPicker.FromHsv(i * 60 % 360, 0.62, 0.92), i / 6.0));
        return hues;
    }

    private static Border GradientSwatch(string start, string? end, string tooltip, bool selected, Action pick)
    {
        var swatch = Swatch(start, tooltip, selected, pick);
        if (end != null && swatch.Child is Ellipse dot) { dot.Fill = new LinearGradientBrush(ThemeManager.Hex(start), ThemeManager.Hex(end), 45); dot.StrokeThickness = 0; }
        return swatch;
    }

    /// <summary>
    /// A colour row: one round swatch per preset (the current one ringed) and a swatch that opens a picker for any
    /// colour, shown live everywhere as it moves.
    /// </summary>
    private void BuildColourRow(System.Windows.Controls.Panel panel, (string Name, string Hex)[] presets, string current, Action<string> set, Action<string> preview)
    {
        bool isPreset = false;
        foreach (var (name, hex) in presets)
        {
            bool selected = current.Equals(hex, StringComparison.OrdinalIgnoreCase);
            isPreset |= selected;
            string chosen = hex;
            panel.Children.Add(Swatch(hex, Loc.T(name), selected, () => set(chosen)));
        }
        panel.Children.Add(Divider());
        Border custom = null!;
        custom = Swatch(isPreset ? "#FFFFFF" : current, Loc.T("Pick any colour"), selected: !isPreset, () => OpenPicker(custom, current, set, preview));
        if (isPreset && custom.Child is Ellipse spectrum) { spectrum.Fill = Spectrum(); spectrum.StrokeThickness = 0; }
        panel.Children.Add(custom);
    }

    /// <summary>
    /// Opens the picker under <paramref name="anchor"/>. Every move repaints the app at once; closing keeps the
    /// colour (and saves it), Esc puts back what was there.
    /// </summary>
    private void OpenPicker(FrameworkElement anchor, string startHex, Action<string> set, Action<string> preview)
    {
        ThemeManager.TryParseAccent(startHex, out var start);
        var picker = new ColourPicker(start, (Style)FindResource("FieldText"));
        var card = new Border
        {
            Child = picker, Padding = new Thickness(16), CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1),
            Margin = new Thickness(14, 6, 14, 20),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.16, Color = Colors.Black, Direction = 270 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        var popup = new Popup
        {
            Child = card, PlacementTarget = anchor, Placement = PlacementMode.Bottom,
            HorizontalOffset = -14, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
        };

        string? picked = null, pending = null;
        bool cancelled = false;
        // Moves arrive faster than the screen redraws: apply the newest one once per frame.
        void OnFrame(object? s, EventArgs e)
        {
            CompositionTarget.Rendering -= OnFrame;
            if (pending != null) { preview(pending); pending = null; }
        }
        picker.Moved += c =>
        {
            picked = ThemeManager.Hex(c);
            if (pending == null) CompositionTarget.Rendering += OnFrame;
            pending = picked;
        };
        card.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { cancelled = true; popup.IsOpen = false; e.Handled = true; }
            else if (e.Key == Key.Enter) { popup.IsOpen = false; e.Handled = true; }
        };
        popup.Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnFrame;
            pending = null;
            _picking = false;
            if (picked == null) return;
            if (cancelled) Commit(Current); // back to what was kept
            else set(picked);
        };
        _picking = true;
        popup.IsOpen = true;
        picker.FocusBox();
    }

    /// <summary>A round colour swatch; the chosen one is ringed. Very pale and very dark colours get a hairline so they show on any page.</summary>
    private static Border Swatch(string hex, string tooltip, bool selected, Action pick)
    {
        ThemeManager.TryParseAccent(hex, out var color);
        int sum = color.R + color.G + color.B;
        var dot = new Ellipse { Fill = new SolidColorBrush(color), StrokeThickness = sum > 3 * 225 || sum < 3 * 60 ? 1 : 0 };
        dot.SetResourceReference(Shape.StrokeProperty, "ToggleTrackBrush");
        var swatch = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(2), Padding = new Thickness(3),
            Margin = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand, ToolTip = tooltip, Background = Brushes.Transparent, Child = dot,
        };
        if (selected) swatch.SetResourceReference(Border.BorderBrushProperty, "InkBrush");
        else swatch.BorderBrush = Brushes.Transparent;
        swatch.MouseLeftButtonUp += (_, _) => pick();
        return swatch;
    }

    // ---- Backdrop ------------------------------------------------------------------------------------------

    private Grid? _backdrop;
    private string? _backdropKey;

    /// <summary>Paints the current theme's backdrop behind the window, cross-fading from the last one.</summary>
    private void UpdateBackdrop()
    {
        var look = ThemeManager.CurrentLook;
        var b = look.Version.Backdrop;
        string key = look.HasBackdrop ? $"{b.Kind}|{string.Join(",", b.Colours)}|{b.Angle}|{b.Image}|{look.Style.Grain}|{look.Dark}|{ThemeManager.Hex(look.Shades.Page)}|{look.Picture?.GetHashCode()}" : "";
        if (key == _backdropKey) return;
        bool had = !string.IsNullOrEmpty(_backdropKey);
        _backdropKey = key;
        if (!had && key.Length == 0) return;
        var layer = new Grid { IsHitTestVisible = false, ClipToBounds = true };
        if (key.Length > 0)
        {
            foreach (var part in BackdropLayers(look, mini: false)) layer.Children.Add(part);
            layer.CacheMode = new BitmapCache(); // drawn once, not on every frame
        }
        Grid.SetColumnSpan(layer, 2);
        System.Windows.Controls.Panel.SetZIndex(layer, -1);
        var old = _backdrop;
        _backdrop = layer;
        RootGrid.Children.Insert(0, layer);
        if (old == null) return;
        if (!IsVisible || ThemeManager.IsPreviewing || !look.Style.Motion) { RootGrid.Children.Remove(old); return; }
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        fade.Completed += (_, _) => RootGrid.Children.Remove(old);
        layer.BeginAnimation(OpacityProperty, fade);
    }

    private static readonly Point[] GlowSpots = [new(0.08, 0.12), new(0.92, 0.06), new(0.88, 0.95), new(0.1, 0.92)];

    /// <summary>The page colour or gradient, the blurred picture under a veil, the glows and the grain.</summary>
    private static IEnumerable<UIElement> BackdropLayers(ThemeManager.Look look, bool mini)
    {
        var s = look.Shades;
        var b = look.Version.Backdrop;
        bool dark = look.Dark;
        if (b.Kind == "linear" && b.Colours.Length > 0)
        {
            double rad = b.Angle * Math.PI / 180;
            var brush = new LinearGradientBrush { StartPoint = new Point(0.5 - Math.Cos(rad) / 2, 0.5 - Math.Sin(rad) / 2), EndPoint = new Point(0.5 + Math.Cos(rad) / 2, 0.5 + Math.Sin(rad) / 2) };
            for (int i = 0; i < b.Colours.Length; i++) brush.GradientStops.Add(new GradientStop(ThemeManager.Hex(b.Colours[i]), b.Colours.Length == 1 ? 0 : (double)i / (b.Colours.Length - 1)));
            yield return new Rectangle { Fill = brush };
        }
        else yield return new Rectangle { Fill = new SolidColorBrush(s.Page) };
        if (look.Picture != null && b.Kind is "wallpaper" or "image")
        {
            var image = new Image { Source = look.Picture, Stretch = Stretch.UniformToFill, Margin = new Thickness(mini ? -6 : -60),
                Effect = new BlurEffect { Radius = mini ? 4 : 40, RenderingBias = RenderingBias.Performance } };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            yield return image;
            yield return new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(dark ? (byte)0xA8 : (byte)0x8C, s.Page.R, s.Page.G, s.Page.B)) };
        }
        if (b.Kind == "glow")
            for (int i = 0; i < b.Colours.Length; i++)
            {
                var c = ThemeManager.Hex(b.Colours[i]);
                var spot = GlowSpots[i % GlowSpots.Length];
                double op = dark ? 0.55 : 0.95;
                var brush = new RadialGradientBrush { Center = spot, GradientOrigin = spot, RadiusX = 0.6, RadiusY = 0.7 };
                brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * op), c.R, c.G, c.B), 0));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(140 * op), c.R, c.G, c.B), 0.45));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
                yield return new Rectangle { Fill = brush };
            }
        if (look.Style.Grain > 0 && !mini)
            yield return new Rectangle
            {
                Fill = new ImageBrush(Noise(dark)) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 128, 128), ViewportUnits = BrushMappingMode.Absolute },
                Opacity = Math.Min(1, look.Style.Grain * 0.8),
            };
    }

    private static BitmapSource? _noiseDark, _noiseLight;

    /// <summary>A tile of random specks (white on dark themes, black on light ones) for a paper-like grain.</summary>
    private static BitmapSource Noise(bool dark)
    {
        ref var cached = ref dark ? ref _noiseDark : ref _noiseLight;
        if (cached != null) return cached;
        const int size = 128;
        var pixels = new byte[size * size * 4];
        var random = new Random(7);
        byte v = dark ? (byte)255 : (byte)0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
            pixels[i + 3] = (byte)random.Next(256);
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return cached = bitmap;
    }
}
