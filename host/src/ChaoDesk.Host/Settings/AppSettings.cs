using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Serilog;

namespace ChaoDesk.Host.Settings;

public enum QualityMode { Auto, High, Low }

/// <summary>Persisted user settings (%LOCALAPPDATA%\ChaoDesk\settings.json).</summary>
public sealed class AppSettings
{
    public static readonly int[] AllowedFps = [30, 60];
    public static readonly double[] AllowedRenderScales = [0.5, 0.75, 1.0];

    public string CurrentWallpaperId { get; set; } = "lotus-pond";

    /// <summary>"system" (follow Windows), "light" or "dark" — see <see cref="ThemeManager"/>.</summary>
    public string Theme { get; set; } = "system";

    /// <summary>"system" (the Windows display language), "en" or "zh" — see <see cref="Localization.Loc"/>.</summary>
    public string Language { get; set; } = "system";

    /// <summary>"auto" (Google, then Microsoft), "google" or "microsoft" — see <see cref="Translate.Translator"/>.</summary>
    public string TranslateEngine { get; set; } = "auto";

    /// <summary>"auto" (Chinese ↔ English) or a language code from <see cref="Translate.Translator.Targets"/>.</summary>
    public string TranslateTarget { get; set; } = "auto";

    /// <summary>Show the interactive wallpaper at all (off: no WebView2, no mouse hook, just the tools).</summary>
    public bool WallpaperEnabled { get; set; } = true;

    // ---- Launcher, clipboard, file search ----

    public const string DefaultLauncherHotkey = "Alt+Space";
    public const string DefaultClipboardHotkey = "Shift+`";
    public static readonly string[] DefaultIgnoredApps = ["KeePass", "KeePassXC", "1Password", "Bitwarden", "Enpass"];

    /// <summary>Opens the launcher; empty turns the hotkey off.</summary>
    public string LauncherHotkey { get; set; } = DefaultLauncherHotkey;

    /// <summary>Opens the clipboard history; empty turns the hotkey off.</summary>
    public string ClipboardHotkey { get; set; } = DefaultClipboardHotkey;

    public bool ClipboardEnabled { get; set; } = true;
    public int ClipboardMaxItems { get; set; } = 500;

    /// <summary>The preview / details column on the right of the clipboard and launcher popups (a button toggles it).</summary>
    public bool ClipboardPreview { get; set; } = true;
    public bool LauncherPreview { get; set; } = true;

    /// <summary>Process names (without .exe) whose copies are never recorded, e.g. password managers.</summary>
    public List<string> ClipboardIgnoredApps { get; set; } = [.. DefaultIgnoredApps];

    // ---- Screenshots ----
    // Defaults: the author's own layout, Shift + a function key for everything that captures. Only new
    // installs get them; saved settings keep whatever the user had.

    public const string DefaultScreenshotHotkey = "Shift+F2";
    public const string DefaultQuickScreenshotHotkey = "Shift+F1";
    public const string DefaultScrollScreenshotHotkey = "Shift+F4";
    public const string DefaultPinHotkey = "Shift+F3";
    public const string DefaultPinsToggleHotkey = "Ctrl+F3";
    public const string DefaultOcrHotkey = "Ctrl+`";

    public string ScreenshotHotkey { get; set; } = DefaultScreenshotHotkey;
    public string QuickScreenshotHotkey { get; set; } = DefaultQuickScreenshotHotkey;
    public string ScrollScreenshotHotkey { get; set; } = DefaultScrollScreenshotHotkey;
    public string PinHotkey { get; set; } = DefaultPinHotkey;
    public string PinsToggleHotkey { get; set; } = DefaultPinsToggleHotkey;

    // ---- Text recognition (OCR) ----

    /// <summary>Choose an area and read the text in it.</summary>
    public string OcrHotkey { get; set; } = DefaultOcrHotkey;

    /// <summary>"auto" (every installed language pack) or one pack's id (Ocr.OcrPacks).</summary>
    public string OcrLanguage { get; set; } = "auto";

    /// <summary>Join wrapped lines into paragraphs (off: keep the lines as they are on screen).</summary>
    public bool OcrMergeLines { get; set; }

    /// <summary>Translate recognised text as soon as the text window has it (off: the Translate button, Ctrl+T).</summary>
    public bool OcrAutoTranslate { get; set; }

    /// <summary>Copy the text at once and show a notice, instead of opening the text window.</summary>
    public bool OcrCopyDirectly { get; set; }

    /// <summary>Where screenshots are saved; empty = Pictures\Chao Desk.</summary>
    public string ScreenshotFolder { get; set; } = "";

    public static readonly int[] AllowedJpegQualities = [0, 95, 90, 80, 70, 60];

    /// <summary>0 = save lossless PNG; otherwise JPEG at this quality. The clipboard always gets the lossless image.</summary>
    public int ScreenshotJpegQuality { get; set; }

    /// <summary>Also save every region screenshot that is copied.</summary>
    public bool ScreenshotAutoSave { get; set; }

    /// <summary>Quick screenshots (whole screen) are saved as well as copied.</summary>
    public bool QuickScreenshotSaves { get; set; } = true;

    /// <summary>Space in Explorer previews the selected file (installs a keyboard hook while on).</summary>
    public bool QuickLookEnabled { get; set; } = true;

    public bool FileSearchEnabled { get; set; } = true;

    /// <summary>With the wallpaper off: keep the widgets (clock, calendar, weather, …) on the desktop over the Windows wallpaper.</summary>
    public bool DesktopWidgets { get; set; } = true;
    public bool FileSearchIncludeHidden { get; set; }

    /// <summary>Leave the contents of node_modules folders and C:\Windows\WinSxS out of the index (saves memory).</summary>
    public bool FileSearchSkipJunk { get; set; } = true;

    /// <summary>Folders whose contents never show up in file search results.</summary>
    public List<string> FileSearchExcluded { get; set; } = new();

    /// <summary>Per-wallpaper user property values: wallpaper id → property id → value.</summary>
    public Dictionary<string, Dictionary<string, JsonNode?>> WallpaperProperties { get; set; } = new();

    public int FpsLimit { get; set; } = 30;
    public double RenderScale { get; set; } = 1.0;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public QualityMode Quality { get; set; } = QualityMode.Auto;

    public bool PauseWhenMaximized { get; set; } = true;
    public bool PauseOnBattery { get; set; } = true;
    public bool MouseInteraction { get; set; } = true;
    public bool StartWithWindows { get; set; }

    /// <summary>Fetch exchange rates and the official holiday schedule (host-side only).</summary>
    public bool OnlineInfo { get; set; } = true;

    /// <summary>Look for a newer release on GitHub once a day; installing always needs a click.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>City for the weather page, e.g. "北京" or "Sydney". Empty: no weather.</summary>
    public string WeatherCity { get; set; } = "";

    /// <summary>Widgets on the wallpaper card, in order (see <see cref="WidgetCatalog.Catalog"/>).</summary>
    public List<WidgetItem> Widgets { get; set; } = new();

    /// <summary>"stack": widgets one below the other; "tabs": one at a time, switched with tabs.</summary>
    public string WidgetLayout { get; set; } = "stack";

    /// <summary>"peek" (default): a see-through clock that opens the card on click; "open": always shown.</summary>
    public string CardDisplay { get; set; } = "peek";

    /// <summary>Most widgets per stacked page (0 = as many as fit on the screen).</summary>
    public int WidgetsPerPage { get; set; }

    public List<ShortcutItem> Shortcuts { get; set; } = new();

    /// <summary>The user's task list, shown on the wallpaper card.</summary>
    public List<TaskItem> Tasks { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Clamp values to the supported options so a hand-edited file can't break anything.</summary>
    public AppSettings Normalize()
    {
        if (!AllowedFps.Contains(FpsLimit)) FpsLimit = 30;
        if (!AllowedRenderScales.Any(s => Math.Abs(s - RenderScale) < 0.001)) RenderScale = 1.0;
        CurrentWallpaperId = string.IsNullOrWhiteSpace(CurrentWallpaperId) ? "lotus-pond" : CurrentWallpaperId;
        if (!ThemeManager.Modes.Contains(Theme)) Theme = "system";
        if (!Localization.Loc.Modes.Contains(Language)) Language = "system";
        if (!Translate.Translator.Engines.Contains(TranslateEngine)) TranslateEngine = "auto";
        if (!Translate.Translator.Targets.Contains(TranslateTarget)) TranslateTarget = "auto";
        LauncherHotkey = NormalizeHotkey(LauncherHotkey, DefaultLauncherHotkey);
        ClipboardHotkey = NormalizeHotkey(ClipboardHotkey, DefaultClipboardHotkey);
        ScreenshotHotkey = NormalizeHotkey(ScreenshotHotkey, DefaultScreenshotHotkey);
        QuickScreenshotHotkey = NormalizeHotkey(QuickScreenshotHotkey, DefaultQuickScreenshotHotkey);
        ScrollScreenshotHotkey = NormalizeHotkey(ScrollScreenshotHotkey, DefaultScrollScreenshotHotkey);
        PinHotkey = NormalizeHotkey(PinHotkey, DefaultPinHotkey);
        PinsToggleHotkey = NormalizeHotkey(PinsToggleHotkey, DefaultPinsToggleHotkey);
        OcrHotkey = NormalizeHotkey(OcrHotkey, DefaultOcrHotkey);
        if (Ocr.OcrPacks.Find(OcrLanguage ?? "") == null) OcrLanguage = "auto";
        if (!AllowedJpegQualities.Contains(ScreenshotJpegQuality)) ScreenshotJpegQuality = 0;
        ScreenshotFolder = (ScreenshotFolder ?? "").Trim();
        if (!Clips.ClipboardHistory.AllowedSizes.Contains(ClipboardMaxItems)) ClipboardMaxItems = 500;
        ClipboardIgnoredApps = (ClipboardIgnoredApps ?? [.. DefaultIgnoredApps]).Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
        FileSearchExcluded = (FileSearchExcluded ?? new()).Select(p => p.Trim().TrimEnd('\\')).Where(p => p.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
        WallpaperProperties ??= new();
        Widgets = WidgetCatalog.Normalize(Widgets);
        if (!WidgetCatalog.Layouts.Contains(WidgetLayout)) WidgetLayout = "stack";
        if (!WidgetCatalog.Displays.Contains(CardDisplay)) CardDisplay = "peek";
        if (!WidgetCatalog.PerPageOptions.Contains(WidgetsPerPage)) WidgetsPerPage = 0;
        Shortcuts = (Shortcuts ?? new()).Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Target)).Take(12).ToList();
        foreach (var x in Shortcuts) { if (string.IsNullOrEmpty(x.Id)) x.Id = Guid.NewGuid().ToString("N"); if (x.Name.Length > 20) x.Name = x.Name[..20]; }
        WeatherCity = (WeatherCity ?? "").Trim();
        if (WeatherCity.Length > 60) WeatherCity = WeatherCity[..60];
        Tasks = (Tasks ?? new()).Where(t => t != null && !string.IsNullOrWhiteSpace(t.Text)).Take(200).ToList();
        foreach (var t in Tasks) { if (string.IsNullOrEmpty(t.Id)) t.Id = Guid.NewGuid().ToString("N"); if (t.Text.Length > 120) t.Text = t.Text[..120]; }
        return this;
    }

    /// <summary>Empty stays empty (hotkey off); unparsable text falls back to the default.</summary>
    private static string NormalizeHotkey(string? text, string fallback)
    {
        if (text == null) return fallback;
        if (text.Trim().Length == 0) return "";
        var key = Input.Hotkey.Parse(text);
        return key.IsUsable ? key.ToString() : fallback;
    }

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                if (s != null) return s.Normalize();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read settings from {Path}; using defaults", path);
            TryBackup(path);
        }
        return new AppSettings();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, path, overwrite: true); // atomic replace: never leaves a half-written file
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not save settings to {Path}", path);
        }
    }

    public Dictionary<string, JsonNode?> PropertiesFor(string wallpaperId)
    {
        if (!WallpaperProperties.TryGetValue(wallpaperId, out var props))
        {
            props = new Dictionary<string, JsonNode?>();
            WallpaperProperties[wallpaperId] = props;
        }
        return props;
    }

    private static void TryBackup(string path)
    {
        try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* best effort */ }
    }
}

/// <summary>A to-do item. Kept short so it fits on the clock card.</summary>
public sealed class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
}
