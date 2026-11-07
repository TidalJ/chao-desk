using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Settings;

/// <summary>
/// The look of the app. A theme is more than colours: each has a light and a dark version (a background — plain,
/// a gradient, soft glows, the wallpaper or a picture — and five base colours) and a style (corner radius, card
/// material, fonts, sidebar, how the selected page is marked, density, grain, the home banner). The user can adjust
/// any theme on top (<see cref="Adjust"/>, kept per theme and undone by "restore"), or save an adjusted theme as
/// one of their own. Everything ends up in one resource dictionary that every window references with
/// DynamicResource; its brushes keep their identity and only change colour (gliding there), so nothing is reloaded
/// and colours picked up once in code stay current. To add a theme: one entry in <see cref="Palettes"/>.
/// </summary>
public static class ThemeManager
{
    public static readonly string[] Modes = ["system", "light", "dark"];

    /// <summary>The five colours a theme version is made of; every other colour is mixed from them.</summary>
    public readonly record struct Shades(Color Page, Color Card, Color Ink, Color Accent);

    /// <summary>
    /// What is behind the page: "solid" (the page colour), "linear" (the colours at <see cref="Angle"/>), "glow"
    /// (soft lights of the colours over the page colour), "wallpaper" (the desktop's picture, blurred) or "image"
    /// (a picture of the user's, blurred).
    /// </summary>
    public sealed record Backdrop(string Kind, string[] Colours, double Angle = 135, string? Image = null);

    /// <summary>One version (light or dark) of a theme. <see cref="Side"/>: the sidebar colour for a "contrast" sidebar.</summary>
    public sealed record Version(Backdrop Backdrop, Shades Shades, Color? AccentEnd = null, Color? Side = null);

    /// <summary>
    /// The style of a theme. Card: solid, glass, outline, raised. Font: sans, serif, geo, mono (titles). Body:
    /// system, title (same as titles), mono. Sidebar: same, contrast, float, icons. Nav: pill, bar, filled, line.
    /// Icons: line, accent, none. Density: compact, default, roomy. Hero: "" (none), a picture or "gradient".
    /// </summary>
    public sealed record Style(double Radius = 18, string Card = "solid", double CardOpacity = 0.7, string Font = "sans", string Body = "system",
        double Zoom = 1, string Sidebar = "same", string Nav = "pill", string Icons = "line", double SidebarWidth = 236, string Density = "default",
        double Grain = 0, string Hero = "", bool Motion = true);

    /// <summary>A theme: id (stored), name (shown), tags for the filter (gradient, dark, minimal, distinctive), versions, style.</summary>
    public sealed record Palette(string Id, string Name, string[] Tags, Version Light, Version Dark, Style Style, bool FollowsWallpaper = false);

    /// <summary>
    /// The user's changes on top of a theme; null means "as the theme has it". <see cref="AccentEnd"/> "" makes a
    /// gradient accent plain. Backgrounds are kept per version, since a light and a dark page want different ones.
    /// </summary>
    public sealed record Adjust(string? Accent = null, string? AccentEnd = null, Backdrop? Light = null, Backdrop? Dark = null,
        double? Radius = null, string? Card = null, double? CardOpacity = null, string? Font = null, string? Body = null, double? Zoom = null,
        string? Sidebar = null, string? Nav = null, string? Icons = null, double? SidebarWidth = null, string? Density = null, double? Grain = null,
        string? Hero = null, bool? Motion = null)
    {
        public static readonly Adjust None = new();

        /// <summary>The names (as shown) of what is adjusted.</summary>
        public IEnumerable<string> Changes()
        {
            if (Accent != null || AccentEnd != null) yield return "Accent";
            if (Light != null || Dark != null) yield return "Background";
            if (Radius != null) yield return "Corners";
            if (Card != null || CardOpacity != null) yield return "Cards";
            if (Font != null || Body != null || Zoom != null) yield return "Text";
            if (Sidebar != null || Nav != null || Icons != null || SidebarWidth != null) yield return "Sidebar";
            if (Density != null || Motion != null) yield return "Density";
            if (Grain != null) yield return "Grain";
            if (Hero != null) yield return "Banner";
        }

        public bool IsEmpty => !Changes().Any();
    }

    /// <summary>A theme of the user's own: a built-in theme with adjustments, under a name.</summary>
    public sealed record UserTheme(string Id, string Name, string Base, Adjust Adjust);

    // ---- The themes ----------------------------------------------------------------------------------------

    private static Shades S(string page, string card, string ink, string accent) => new(Hex(page), Hex(card), Hex(ink), Hex(accent));
    private static Backdrop Solid => new("solid", []);
    private static Backdrop Glow(params string[] c) => new("glow", c);
    private static Backdrop Linear(double angle, params string[] c) => new("linear", c, angle);
    private static Color? C(string hex) => Hex(hex);

    public static readonly Palette[] Palettes =
    [
        new("celadon", "Celadon", ["minimal"],
            new(Solid, S("#F5F8F6", "#FFFFFF", "#1C2522", "#2E6B5E")),
            new(Solid, S("#161B19", "#1E2522", "#E6EDEA", "#74B8A6")),
            new(Hero: "home-hero.jpg")),
        new("aurora", "Aurora", ["gradient", "dark"],
            new(Glow("#BFEBDF", "#CCDDF8", "#D3F1E7"), S("#F3F7F8", "#FFFFFF", "#17201F", "#11977F"), C("#2F7FE0")),
            new(Glow("#1FA88E", "#2F6FD0", "#14806F"), S("#0D1315", "#172022", "#E9F1EF", "#5EE0C0"), C("#4FA3F7")),
            new(Radius: 22, Card: "glass", CardOpacity: 0.55, Sidebar: "float", Grain: 0.03)),
        new("peach", "Peach", ["gradient"],
            new(Linear(140, "#FFE9DE", "#FCE3EC", "#E9E6FB"), S("#FCEBEB", "#FFFFFF", "#2B1F26", "#F07A5E"), C("#E5577E")),
            new(Linear(140, "#2A1A22", "#1E1726", "#16182A"), S("#1F1824", "#2A2030", "#F3E8EE", "#FF9E80"), C("#F06A8E")),
            new(Radius: 26, Card: "glass", CardOpacity: 0.62, Font: "geo", Body: "title", Nav: "filled", Grain: 0.02)),
        new("sunset", "Sunset", ["gradient", "distinctive"],
            new(Solid, S("#F4F2EF", "#FFFFFF", "#1E1B1A", "#F2774F"), C("#E8566F"), C("#1C1A22")),
            new(Solid, S("#141217", "#1E1B22", "#EDEAF2", "#FF9A5A"), C("#F2617A"), C("#0E0D11")),
            new(Radius: 16, Card: "raised", Sidebar: "contrast", Hero: "gradient")),
        new("mist", "Morning mist", ["gradient"],
            new(Glow("#D9EEE4", "#DFE7F4"), S("#F2F6F4", "#FFFFFF", "#1C2522", "#2A6A5C")),
            new(Glow("#1F3D35", "#1C2638"), S("#131917", "#1B2321", "#E4ECE9", "#6FBFA9")),
            new(Card: "glass", CardOpacity: 0.72, Grain: 0.05)),
        new("dusk", "Dusk", ["gradient", "dark"],
            new(Glow("#FBE0CF", "#E0E7F3"), S("#F6F5F8", "#FFFFFF", "#1E1F26", "#D2683F"), C("#E9A35E")),
            new(Glow("#22344A", "#5A3B3A"), S("#121820", "#1A212B", "#ECEFF3", "#F2B36B"), C("#E07A5F")),
            new(Radius: 16, Card: "glass", CardOpacity: 0.7, Grain: 0.04)),
        new("lagoon", "Lagoon", ["gradient"],
            new(Linear(150, "#D8F3F1", "#DDE9FB", "#F1F5FA"), S("#E8F2F6", "#FFFFFF", "#14222B", "#0F9D8F"), C("#3B82F6")),
            new(Glow("#0F766E", "#1E3A8A"), S("#0B1418", "#132027", "#E2F1F3", "#2DD4BF"), C("#60A5FA")),
            new(Radius: 16, Card: "glass", CardOpacity: 0.7, Nav: "bar")),
        new("neon", "Neon", ["gradient", "dark", "distinctive"],
            new(Glow("#FFD1E8", "#CFF5FF"), S("#FBF8FF", "#FFFFFF", "#1C1530", "#E0267F"), C("#0091C2")),
            new(Glow("#C2186B", "#007FA3"), S("#0E0B17", "#18142A", "#F1ECFF", "#FF3D9A"), C("#00D1FF")),
            new(Radius: 12, Card: "glass", CardOpacity: 0.5, Font: "geo", Nav: "filled", Grain: 0.03)),
        new("editorial", "Editorial", ["minimal", "distinctive"],
            new(Solid, S("#FBFAF7", "#FBFAF7", "#16130F", "#B23A2A")),
            new(Solid, S("#151311", "#1B1916", "#EFEAE2", "#E0705F")),
            new(Radius: 2, Card: "outline", Font: "serif", Nav: "bar", Density: "roomy", Grain: 0.03)),
        new("terminal", "Terminal", ["dark", "distinctive"],
            new(Solid, S("#F4F6F1", "#FFFFFF", "#1B2A20", "#1F8A4C"), null, C("#E6EBE2")),
            new(Solid, S("#0B0D0C", "#0F1311", "#D7E5DC", "#7CF3A8"), null, C("#0F1311")),
            new(Radius: 6, Card: "outline", Font: "mono", Body: "mono", Sidebar: "icons", Nav: "filled", Density: "compact")),
        new("nordic", "Nordic", ["minimal"],
            new(Solid, S("#EEF1F4", "#FFFFFF", "#1D2530", "#5E81AC"), null, C("#2B3644")),
            new(Solid, S("#1B222C", "#232C38", "#E5EAF0", "#88A9D1"), null, C("#141A22")),
            new(Radius: 12, Sidebar: "contrast", Nav: "bar")),
        new("ink", "Rice paper", ["minimal", "distinctive"],
            new(Solid, S("#FAFAF8", "#FFFFFF", "#141414", "#141414")),
            new(Solid, S("#121212", "#1A1A1A", "#ECECEA", "#E6E6E3")),
            new(Radius: 8, Card: "glass", CardOpacity: 0.65, Font: "serif", Grain: 0.06)),
        new("lotus", "Summer", ["minimal"],
            new(Solid, S("#F6F8F3", "#FFFFFF", "#1F261C", "#C2416E")),
            new(Solid, S("#171A16", "#20241E", "#E8EDE4", "#E68AAB")),
            new(Hero: "hero-summer.jpg")),
        new("autumn", "Autumn pond", ["minimal"],
            new(Solid, S("#F5F6F5", "#FFFFFF", "#22241F", "#B4641E")),
            new(Solid, S("#181917", "#212320", "#ECEDE9", "#E39A55")),
            new(Hero: "hero-autumn.jpg")),
        new("wallpaper", "From wallpaper", ["gradient"],
            new(new("wallpaper", []), S("#F5F7F4", "#FFFFFF", "#1F261C", "#3F7D4E")),
            new(new("wallpaper", []), S("#151814", "#1E221D", "#E8EDE4", "#8DC79A")),
            new(Radius: 16, Card: "glass", CardOpacity: 0.66, Grain: 0.03), FollowsWallpaper: true),
    ];

    /// <summary>Ready-made accents (plain).</summary>
    public static readonly (string Name, string Hex)[] Accents =
    [
        ("Celadon", "#2E6B5E"), ("Ocean", "#1F6FB2"), ("Indigo", "#4655C8"), ("Rouge", "#B03A55"), ("Amber", "#B4730E"), ("Ink", "#2B2F33"),
    ];

    /// <summary>Ready-made gradient accents (start, end).</summary>
    public static readonly (string Name, string Start, string End)[] GradientAccents =
    [
        ("Aurora", "#2BC4A3", "#3E8EF0"), ("Peach", "#FF8A65", "#E5577E"), ("Neon", "#FF3D9A", "#00B8E6"), ("Sunset", "#F7A440", "#E8566F"),
    ];

    /// <summary>Ready-made page colours for a background of one's own.</summary>
    public static readonly (string Name, string Hex)[] Backgrounds =
    [
        ("White", "#FFFFFF"), ("Light grey", "#F3F4F5"), ("Mist blue", "#EEF2F7"), ("Mint", "#EEF5F1"), ("Graphite", "#1E2124"), ("Deep sea", "#12202B"),
    ];

    // ---- State ---------------------------------------------------------------------------------------------

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private static string _mode = "system";
    private static string _themeId = "celadon";
    private static Dictionary<string, Adjust> _adjustments = new();
    private static List<UserTheme> _users = new();
    private static Adjust? _preview;
    private static bool _hooked;
    private static bool _initialised;
    private static bool _modeDark;
    private static Look _look = null!;

    /// <summary>Everything worked out for now: the version (after adjustments), the style, the picture and light or dark.</summary>
    public sealed record Look(Version Version, Style Style, ImageSource? Picture, bool Dark)
    {
        public Shades Shades => Version.Shades;
        public bool HasBackdrop => Version.Backdrop.Kind != "solid" || Style.Grain > 0;
    }

    public static bool IsDark { get; private set; }

    /// <summary>The look in use now.</summary>
    public static Look CurrentLook => _look ?? Resolve(Palettes[0], Adjust.None, false);

    /// <summary>The style in use now.</summary>
    public static Style CurrentStyle => CurrentLook.Style;

    /// <summary>Theme id in use: a built-in id or a user theme's.</summary>
    public static string CurrentId => _themeId;

    /// <summary>The built-in theme in use (for a user theme: the one it is based on).</summary>
    public static Palette Current => FindPalette(_users.FirstOrDefault(u => u.Id == _themeId)?.Base ?? _themeId);

    /// <summary>The user theme in use, or null.</summary>
    public static UserTheme? CurrentUser => _users.FirstOrDefault(u => u.Id == _themeId);

    /// <summary>The adjustments of the theme in use.</summary>
    public static Adjust CurrentAdjust => CurrentUser?.Adjust ?? _adjustments.GetValueOrDefault(_themeId) ?? Adjust.None;

    public static IReadOnlyDictionary<string, Adjust> Adjustments => _adjustments;
    public static IReadOnlyList<UserTheme> Users => _users;

    /// <summary>True while <see cref="Changed"/> reports a live preview (a slider or picker being dragged).</summary>
    public static bool IsPreviewing { get; private set; }

    /// <summary>Raised after the colours changed (windows that built parts in code may want to rebuild them).</summary>
    public static event Action? Changed;

    /// <summary>Where the wallpaper background takes its picture (set by the app: the pond's scene or the Windows wallpaper).</summary>
    public static Func<string?>? WallpaperSource { get; set; }

    public static Palette FindPalette(string? id) => Palettes.FirstOrDefault(p => p.Id == id) ?? Palettes[0];

    public static bool Exists(string? id) => Palettes.Any(p => p.Id == id) || _users.Any(u => u.Id == id);

    /// <summary>Loads the user's adjustments and own themes (no rebuild).</summary>
    public static void Configure(IDictionary<string, Adjust>? adjustments, IEnumerable<UserTheme>? users)
    {
        _adjustments = adjustments?.Where(a => Palettes.Any(p => p.Id == a.Key) && !a.Value.IsEmpty).ToDictionary(a => a.Key, a => a.Value) ?? new();
        _users = users?.Where(u => !string.IsNullOrWhiteSpace(u.Id)).ToList() ?? new();
    }

    public static void SetTheme(string id)
    {
        _themeId = Exists(id) ? id : "celadon";
        _preview = null;
        if (_initialised) Rebuild();
    }

    /// <summary>Sets the adjustments of the theme in use (for a user theme: changes it).</summary>
    public static void SetAdjust(Adjust adjust)
    {
        _preview = null;
        if (CurrentUser is { } user) _users[_users.IndexOf(user)] = user with { Adjust = adjust };
        else if (adjust.IsEmpty) _adjustments.Remove(_themeId);
        else _adjustments[_themeId] = adjust;
        if (_initialised) Rebuild();
    }

    /// <summary>Shows adjustments without keeping them (a slider or picker being dragged): no glide.</summary>
    public static void Preview(Adjust adjust)
    {
        _preview = adjust;
        if (_initialised) Rebuild(glide: false, preview: true);
    }

    /// <summary>Saves the theme in use, with its adjustments, as a theme of the user's own and switches to it.</summary>
    public static UserTheme SaveAsUserTheme(string name)
    {
        var theme = new UserTheme("my-" + Guid.NewGuid().ToString("N")[..8], name, Current.Id, CurrentAdjust);
        _users.Add(theme);
        if (CurrentUser == null) _adjustments.Remove(_themeId);
        _themeId = theme.Id;
        if (_initialised) Rebuild();
        return theme;
    }

    public static void DeleteUserTheme(string id)
    {
        var theme = _users.FirstOrDefault(u => u.Id == id);
        if (theme == null) return;
        _users.Remove(theme);
        if (_themeId == id) { _themeId = theme.Base; if (_initialised) Rebuild(); }
    }

    /// <summary>The wallpaper changed (another pond scene, wallpaper on or off): wallpaper backgrounds follow.</summary>
    public static void WallpaperChanged()
    {
        _wallpaperKey = null;
        if (_initialised && _look?.Version.Backdrop.Kind == "wallpaper") Rebuild();
    }

    public static void Apply(string mode)
    {
        _mode = Modes.Contains(mode) ? mode : "system";
        if (!_hooked)
        {
            _hooked = true;
            // Title bars of windows created later follow the theme too.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => { if (s is Window w) ApplyFrame(w); }));
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _mode == "system")
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply("system"));
            };
        }
        bool dark = _mode == "dark" || (_mode == "system" && WindowsUsesDarkApps());
        if (dark == _modeDark && Application.Current.Resources.MergedDictionaries.Count > 0 && _initialised) return;
        _initialised = true;
        _modeDark = dark;
        Rebuild();
    }

    public static string Mode => _mode;

    /// <summary>Whether the light/dark setting asks for dark now (a background of one's own may still turn a version dark).</summary>
    public static bool IsModeDark => _modeDark;

    // ---- Working out the look ------------------------------------------------------------------------------

    /// <summary>A theme with adjustments, in the light or dark version.</summary>
    public static Look Resolve(Palette p, Adjust a, bool dark)
    {
        var s = p.Style;
        var style = s with
        {
            Radius = a.Radius ?? s.Radius, Card = a.Card ?? s.Card, CardOpacity = a.CardOpacity ?? s.CardOpacity, Font = a.Font ?? s.Font,
            Body = a.Body ?? s.Body, Zoom = a.Zoom ?? s.Zoom, Sidebar = a.Sidebar ?? s.Sidebar, Nav = a.Nav ?? s.Nav, Icons = a.Icons ?? s.Icons,
            SidebarWidth = a.SidebarWidth ?? s.SidebarWidth, Density = a.Density ?? s.Density, Grain = a.Grain ?? s.Grain, Hero = a.Hero ?? s.Hero,
            Motion = a.Motion ?? s.Motion,
        };
        var v = dark ? p.Dark : p.Light;
        ImageSource? picture = null;
        if (p.FollowsWallpaper) { var w = WallpaperVersion(dark, v); v = w.Version; picture = w.Picture; }

        var backdrop = (dark ? a.Dark : a.Light) ?? v.Backdrop;
        var sh = v.Shades;
        bool isDark = dark;
        if (backdrop != v.Backdrop && backdrop.Kind is "solid" or "linear" && backdrop.Colours.Length > 0)
        {
            // A page colour of one's own: cards, text and the sidebar fitted to it, light or dark by its lightness.
            var page = Average(backdrop.Colours);
            isDark = Lightness(page) < 0.2;
            sh = isDark
                ? new Shades(page, Mix(page, Colors.White, 0.06), isDark == dark ? sh.Ink : Hex("#E8EAEC"), sh.Accent)
                : new Shades(page, Colors.White, isDark == dark ? sh.Ink : Hex("#1E2125"), sh.Accent);
            if (isDark != dark) v = v with { Side = null };
        }
        if (backdrop.Kind is "wallpaper" && picture == null) picture = WallpaperVersion(isDark, v).Picture;
        if (backdrop.Kind is "image" && backdrop.Image is { } path) picture = LoadPicture(path);

        Color accent = sh.Accent;
        Color? end = v.AccentEnd;
        if (TryParseAccent(a.Accent, out var own))
        {
            accent = isDark && Lightness(own) < 0.3 ? Mix(own, Colors.White, 0.35) : !isDark && Lightness(own) > 0.5 ? Mix(own, Colors.Black, 0.3) : own;
            end = null;
        }
        if (a.AccentEnd != null) end = TryParseAccent(a.AccentEnd, out var e) ? (isDark && Lightness(e) < 0.3 ? Mix(e, Colors.White, 0.35) : e) : null;
        return new Look(v with { Backdrop = backdrop, Shades = sh with { Accent = accent }, AccentEnd = end }, style, picture, isDark);
    }

    private static void Rebuild(bool glide = true, bool preview = false)
    {
        bool wasDark = IsDark;
        var palette = Current;
        var adjust = _preview ?? CurrentAdjust;
        if (CurrentUser is { } user && _preview == null) adjust = user.Adjust;
        _look = Resolve(palette, adjust, _modeDark);
        IsDark = _look.Dark;
        var colours = new ResourceDictionary { Source = new Uri(IsDark ? "/ChaoDesk;component/Themes/Dark.xaml" : "/ChaoDesk;component/Themes/Light.xaml", UriKind.Relative) };
        Paint(colours, _look);
        Install(colours, glide && _look.Style.Motion && Application.Current.Windows.Cast<Window>().Any(w => w.IsVisible));
        if (wasDark != IsDark || !preview) foreach (Window w in Application.Current.Windows) ApplyFrame(w);
        IsPreviewing = preview;
        try { Changed?.Invoke(); }
        finally { IsPreviewing = false; }
    }

    /// <summary>Writes a look's colours and shapes over the theme dictionary: surfaces as given, the rest mixed from them.</summary>
    private static void Paint(ResourceDictionary d, Look look)
    {
        var s = look.Shades;
        var st = look.Style;
        bool dark = look.Dark, backdrop = look.HasBackdrop;
        Color Toward(Color from, double t) => Mix(from, s.Ink, t);
        Color white = Colors.White, black = Colors.Black;

        Set(d, "PaperBrush", s.Page);
        Set(d, "PageBrush", s.Page);
        Set(d, "CardBrush", s.Card);
        Set(d, "PopupBrush", s.Card);
        Set(d, "FooterBrush", s.Card);
        Set(d, "PreviewBrush", s.Card);
        Set(d, "ControlBrush", s.Card);
        Set(d, "InkBrush", s.Ink);
        Set(d, "MutedBrush", Mix(s.Ink, s.Page, dark ? 0.38 : 0.45));
        Set(d, "PlaceholderBrush", Mix(s.Ink, s.Page, dark ? 0.55 : 0.62));
        Color line = Toward(s.Page, dark ? 0.12 : 0.085);
        Set(d, "LineBrush", line);
        Set(d, "TileBrush", Toward(s.Card, dark ? 0.07 : 0.055));
        Set(d, "KeycapBrush", Toward(s.Card, dark ? 0.07 : 0.055));
        Set(d, "SelectedRowBrush", Toward(s.Card, dark ? 0.09 : 0.065));
        Set(d, "ChartRestBrush", Toward(s.Card, dark ? 0.14 : 0.11));
        Set(d, "ToggleTrackBrush", Toward(s.Card, dark ? 0.18 : 0.16));
        Set(d, "ToggleBorderBrush", Toward(s.Card, dark ? 0.18 : 0.16));
        Set(d, "ControlHoverBrush", Toward(s.Card, dark ? 0.06 : 0.045));
        Set(d, "ControlPressedBrush", Toward(s.Card, dark ? 0.10 : 0.095));
        Set(d, "HoverBrush", dark ? Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0A, s.Ink.R, s.Ink.G, s.Ink.B));
        var veil = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        byte[] alphas = dark ? [0xF2, 0xD9, 0x66, 0x1A] : [0xF5, 0xDB, 0x40, 0x00];
        double[] offsets = dark ? [0, 0.34, 0.62, 0.85] : [0, 0.34, 0.62, 0.8];
        for (int i = 0; i < alphas.Length; i++)
            veil.GradientStops.Add(new GradientStop(Color.FromArgb(alphas[i], s.Page.R, s.Page.G, s.Page.B), offsets[i]));
        veil.Freeze();
        d["HeroVeilBrush"] = veil;
        d["ShadowColor"] = dark ? black : s.Ink;

        // Accent: hover darker (lighter on dark pages), soft tints, text on it white or ink by its lightness.
        Color accent = s.Accent, end = look.Version.AccentEnd ?? accent;
        Color on = Lightness(Mix(accent, end, 0.5)) > 0.5 ? Color.FromRgb(0x11, 0x14, 0x13) : white;
        Set(d, "AccentBrush", accent);
        Set(d, "AccentHoverBrush", dark ? Mix(accent, white, 0.15) : Mix(accent, black, 0.14));
        Set(d, "AccentSoftBrush", Mix(s.Page, accent, dark ? 0.22 : 0.10));
        Set(d, "AccentFaintBrush", Mix(s.Page, accent, dark ? 0.34 : 0.22));
        Set(d, "OnAccentBrush", on);
        Gradient(d, "AccentFillBrush", accent, end);

        // Cards: material, corners, padding.
        byte alpha = st.Card switch { "glass" => (byte)Math.Round(255 * Math.Clamp(st.CardOpacity, 0.2, 1)), "outline" => (byte)0, _ => (byte)255 };
        if (st.Card == "outline" && !backdrop) alpha = 0;
        Set(d, "SurfaceBrush", Color.FromArgb(alpha, s.Card.R, s.Card.G, s.Card.B));
        Set(d, "CardBorderBrush", st.Card switch
        {
            "raised" => Color.FromArgb(0, s.Page.R, s.Page.G, s.Page.B),
            "outline" => Toward(s.Page, dark ? 0.2 : 0.16),
            "glass" when !dark => Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF),
            "glass" => Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF),
            _ => line,
        });
        if (st.Card == "raised")
        {
            var shadow = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 5, Direction = 270, Opacity = dark ? 0.45 : 0.09, Color = dark ? black : Mix(s.Ink, black, 0.5) };
            shadow.Freeze();
            d["CardEffect"] = shadow;
        }
        else d["CardEffect"] = null;
        double r = Math.Clamp(st.Radius, 0, 32);
        d["CardRadius"] = new CornerRadius(r);
        d["ControlRadius"] = new CornerRadius(r >= 14 ? 18 : Math.Max(2, r * 0.7));
        d["ToggleRadius"] = new CornerRadius(r >= 10 ? 12 : Math.Max(2, r * 0.6));
        d["ThumbRadius"] = new CornerRadius(r >= 10 ? 9 : Math.Max(1.5, r * 0.45));
        d["SmallRadius"] = new CornerRadius(Math.Min(r, 10));
        (Thickness row, Thickness pad, Thickness nav) = st.Density switch
        {
            "compact" => (new Thickness(0, 7, 0, 7), new Thickness(18, 2, 18, 2), new Thickness(12, 5, 12, 5)),
            "roomy" => (new Thickness(0, 16, 0, 16), new Thickness(28, 10, 28, 10), new Thickness(12, 11, 12, 11)),
            _ => (new Thickness(0, 12, 0, 12), new Thickness(22, 6, 22, 6), new Thickness(12, 8, 12, 8)),
        };
        d["RowMargin"] = row;
        d["CardPadding"] = pad;
        d["NavItemPadding"] = nav;

        // Fonts.
        d["TitleFont"] = new FontFamily(FontOf(st.Font));
        d["GreetingFont"] = new FontFamily(st.Font == "sans" ? FontOf("serif") : FontOf(st.Font));
        d["BodyFont"] = new FontFamily(st.Body switch { "title" => FontOf(st.Font == "serif" ? "sans" : st.Font, body: true), "mono" => FontOf("mono"), _ => FontOf("sans", body: true) });

        // Sidebar: its background, text and how the current page is marked.
        bool contrast = st.Sidebar == "contrast";
        Color side = contrast ? look.Version.Side ?? (dark ? Mix(s.Page, white, 0.05) : Mix(Hex("#1C1F26"), accent, 0.1)) : s.Page;
        bool sideDark = contrast ? Lightness(side) < 0.25 : dark;
        Color sideInk = contrast ? (sideDark ? Hex("#EDEFF3") : s.Ink) : s.Ink;
        Color sideMuted = Mix(sideInk, side, sideDark ? 0.4 : 0.45);
        Color sideAccent = contrast && sideDark && Lightness(accent) < 0.3 ? Mix(accent, white, 0.4) : accent;
        Color clear = Color.FromArgb(0, side.R, side.G, side.B);
        Set(d, "SidebarBrush", st.Sidebar switch
        {
            "contrast" => side,
            "float" => Color.FromArgb(dark ? (byte)0x1A : (byte)0xB8, dark ? (byte)255 : s.Card.R, dark ? (byte)255 : s.Card.G, dark ? (byte)255 : s.Card.B),
            "icons" when !backdrop => Mix(s.Page, s.Card, 0.5),
            _ => backdrop ? clear : s.Page,
        });
        Set(d, "SideLineBrush", st.Sidebar switch { "contrast" => clear, "float" => st.Card == "glass" || dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : line, _ => line });
        Set(d, "SideInkBrush", sideInk);
        Set(d, "SideMutedBrush", sideMuted);
        Set(d, "SideHoverBrush", sideDark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0A, s.Ink.R, s.Ink.G, s.Ink.B));
        Set(d, "NavGlyphBrush", st.Icons == "accent" ? sideAccent : sideMuted);
        d["NavGlyphVisibility"] = st.Icons == "none" ? Visibility.Collapsed : Visibility.Visible;
        d["NavLabelVisibility"] = st.Sidebar == "icons" ? Visibility.Collapsed : Visibility.Visible;
        Color pillBg = contrast ? Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF) : st.Sidebar == "float" && dark ? Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF) : s.Card;
        Color none = Color.FromArgb(0, accent.R, accent.G, accent.B);
        switch (st.Nav)
        {
            case "filled":
                Gradient(d, "NavOnBrush", accent, end);
                Set(d, "NavOnBorderBrush", none);
                Set(d, "NavOnInkBrush", on);
                Set(d, "NavOnGlyphBrush", on);
                break;
            case "bar":
            case "line":
                Gradient(d, "NavOnBrush", Color.FromArgb(0, pillBg.R, pillBg.G, pillBg.B), Color.FromArgb(0, pillBg.R, pillBg.G, pillBg.B));
                Set(d, "NavOnBorderBrush", none);
                Set(d, "NavOnInkBrush", contrast ? sideInk : st.Nav == "bar" ? sideAccent : sideInk);
                Set(d, "NavOnGlyphBrush", sideAccent);
                break;
            default:
                Gradient(d, "NavOnBrush", pillBg, pillBg);
                Set(d, "NavOnBorderBrush", contrast ? Color.FromArgb(0, 0, 0, 0) : line);
                Set(d, "NavOnInkBrush", sideInk);
                Set(d, "NavOnGlyphBrush", sideAccent);
                break;
        }
        Set(d, "NavBarBrush", st.Nav == "bar" ? sideAccent : none);
        Set(d, "NavLineBrush", st.Nav == "line" ? sideAccent : none);
        Set(d, "NavSelectedBrush", pillBg);
    }

    private static string FontOf(string font, bool body = false) => font switch
    {
        "serif" => "Georgia, Noto Serif SC, Source Han Serif SC, SimSun",
        "geo" => "Bahnschrift, Microsoft YaHei UI",
        "mono" => "Cascadia Mono, Consolas, Microsoft YaHei UI",
        _ => body ? "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI" : "Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI",
    };

    private static void Gradient(ResourceDictionary d, string key, Color a, Color b)
    {
        var brush = new LinearGradientBrush(a, b, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        d[key] = brush;
    }

    // ---- Installing ----------------------------------------------------------------------------------------

    private const string LiveMarker = "ChaoDesk.LiveTheme";
    private static readonly Duration GlideTime = new(TimeSpan.FromMilliseconds(220));

    /// <summary>Copies a freshly worked-out dictionary into the live one (installing it the first time).</summary>
    private static void Install(ResourceDictionary fresh, bool glide)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count == 0 || merged[0][LiveMarker] is not SolidColorBrush { IsFrozen: false } marker || !marker.CheckAccess())
        {
            // Own, changeable brushes, so later themes can recolour them in place.
            var live = new ResourceDictionary();
            foreach (var key in fresh.Keys) live[key] = Thaw(fresh[key]);
            live[LiveMarker] = new SolidColorBrush();
            if (merged.Count > 0) merged[0] = live; else merged.Add(live);
            return;
        }
        var target = merged[0];
        foreach (var key in fresh.Keys)
        {
            object? value = fresh[key], current = target.Contains(key) ? target[key] : null;
            if (current is SolidColorBrush { IsFrozen: false } brush && value is SolidColorBrush next)
                Recolour(brush, next.Color, glide);
            else if (current is GradientBrush { IsFrozen: false } gradient && value is GradientBrush nextGradient
                     && gradient.GradientStops.Count == nextGradient.GradientStops.Count)
                for (int i = 0; i < gradient.GradientStops.Count; i++)
                {
                    gradient.GradientStops[i].Color = nextGradient.GradientStops[i].Color;
                    gradient.GradientStops[i].Offset = nextGradient.GradientStops[i].Offset;
                }
            else if (!Same(current, value))
                target[key] = value == null ? null : Thaw(value);
        }
    }

    private static bool Same(object? a, object? b) => a switch
    {
        null => b == null,
        FontFamily f => b is FontFamily g && f.Source == g.Source,
        DropShadowEffect e => b is DropShadowEffect x && e.Opacity == x.Opacity && e.Color == x.Color,
        _ => Equals(a, b),
    };

    private static object Thaw(object value) => value switch
    {
        SolidColorBrush b => new SolidColorBrush(b.Color),
        DropShadowEffect e => e, // shared and frozen: effects are not recoloured
        Freezable f => f.Clone(),
        _ => value,
    };

    private static void Recolour(SolidColorBrush brush, Color to, bool glide)
    {
        Color from = brush.Color; // the colour on screen, mid-glide included
        brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        brush.Color = to;
        if (glide && from != to)
            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new System.Windows.Media.Animation.ColorAnimation(from, to, GlideTime)
                {
                    FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
                });
    }

    // ---- Pictures --------------------------------------------------------------------------------------------

    private static string? _wallpaperKey;
    private static (Version Light, Version Dark, ImageSource? Picture) _wallpaper;

    /// <summary>
    /// The version for a wallpaper background: the page tinted by the picture's average colour and the accent its
    /// most vivid colour, darkened (or lifted on dark pages) until text on it reads.
    /// </summary>
    private static (Version Version, ImageSource? Picture) WallpaperVersion(bool dark, Version fallback)
    {
        string? path = null;
        try { path = WallpaperSource?.Invoke(); } catch { }
        string key = path ?? "";
        if (_wallpaperKey != key)
        {
            _wallpaperKey = key;
            var palette = Palettes.First(p => p.FollowsWallpaper);
            _wallpaper = AnalysePicture(path) is { } a ? a : (palette.Light, palette.Dark, null);
        }
        return _wallpaper.Picture == null ? (fallback, null) : (dark ? _wallpaper.Dark : _wallpaper.Light, _wallpaper.Picture);
    }

    private static (Version, Version, ImageSource?)? AnalysePicture(string? path)
    {
        if (LoadPicture(path, 64) is not BitmapSource small) return null;
        var px = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        int w = px.PixelWidth, h = px.PixelHeight;
        var data = new byte[w * h * 4];
        px.CopyPixels(data, w * 4, 0);
        double ar = 0, ag = 0, ab = 0;
        var weight = new double[12];
        var sums = new double[12, 3];
        for (int i = 0; i < data.Length; i += 4)
        {
            var c = Color.FromRgb(data[i + 2], data[i + 1], data[i]);
            ar += c.R; ag += c.G; ab += c.B;
            var (hue, s, v) = ToHsv(c);
            if (s < 0.25 || v < 0.25) continue;
            int b = (int)(hue / 30) % 12;
            double wgt = s * s * v;
            weight[b] += wgt;
            sums[b, 0] += c.R * wgt; sums[b, 1] += c.G * wgt; sums[b, 2] += c.B * wgt;
        }
        int n = w * h;
        var avg = Color.FromRgb((byte)(ar / n), (byte)(ag / n), (byte)(ab / n));
        int best = Array.IndexOf(weight, weight.Max());
        Color vivid = weight[best] > 0
            ? Color.FromRgb((byte)(sums[best, 0] / weight[best]), (byte)(sums[best, 1] / weight[best]), (byte)(sums[best, 2] / weight[best]))
            : Hex("#3F7D4E");
        Color lightAccent = vivid, darkAccent = vivid;
        for (int i = 0; i < 12 && Lightness(lightAccent) > 0.18; i++) lightAccent = Mix(lightAccent, Colors.Black, 0.12);
        for (int i = 0; i < 12 && Lightness(darkAccent) < 0.35; i++) darkAccent = Mix(darkAccent, Colors.White, 0.12);
        Color lightPage = Mix(Hex("#F6F7F5"), avg, 0.12), darkPage = Mix(Hex("#121412"), avg, 0.14);
        var backdrop = new Backdrop("wallpaper", []);
        return (new Version(backdrop, new Shades(lightPage, Colors.White, Mix(Hex("#1C201C"), avg, 0.08), lightAccent)),
                new Version(backdrop, new Shades(darkPage, Mix(darkPage, Colors.White, 0.06), Hex("#E8ECE8"), darkAccent)),
                LoadPicture(path, 96));
    }

    private static readonly Dictionary<string, ImageSource> _pictures = new();

    /// <summary>A picture decoded small (it is shown blurred), or null when it cannot be read.</summary>
    private static ImageSource? LoadPicture(string? path, int width = 96)
    {
        if (path == null) return null;
        string key = width + "|" + path;
        if (_pictures.TryGetValue(key, out var cached)) return cached;
        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = width;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return _pictures[key] = bmp;
        }
        catch { return null; }
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    public static bool TryParseAccent(string? text, out Color color)
    {
        color = default;
        text = text?.Trim() ?? "";
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length != 7 || !int.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static void Set(ResourceDictionary res, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        res[key] = brush;
    }

    internal static Color Hex(string hex) => TryParseAccent(hex, out var c) ? c : Colors.Magenta;

    internal static Color Average(IEnumerable<string> hexes)
    {
        var colours = hexes.Select(Hex).ToList();
        if (colours.Count == 0) return Colors.White;
        return Color.FromRgb((byte)colours.Average(c => c.R), (byte)colours.Average(c => c.G), (byte)colours.Average(c => c.B));
    }

    internal static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>Relative luminance (0 black … 1 white).</summary>
    internal static double Lightness(Color c)
    {
        static double Lin(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    internal static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min, h = 0;
        if (d > 0) h = ((max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4) * 60 + 360) % 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static bool WindowsUsesDarkApps()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private static void ApplyFrame(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }
}
