using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChaoDesk.Host.Desktop;
using ChaoDesk.Host.Localization;
using Serilog;

namespace ChaoDesk.Host.Capture;

/// <summary>
/// All pinned images: creating them, hiding and showing them together (a short staggered fade), and keeping
/// them — position, zoom, opacity and the marks drawn on them — in <c>%LOCALAPPDATA%\ChaoDesk\pins</c>, so they
/// come back after a restart. UI thread only.
/// </summary>
public sealed class PinManager
{
    private sealed record Saved(string Id, int X, int Y, double Zoom, double Opacity);
    private sealed record SavedAll(bool Hidden, List<Saved> Pins);

    private static string Folder => Path.Combine(AppPaths.DataRoot, "pins");
    private static string IndexFile => Path.Combine(Folder, "pins.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly List<PinWindow> _pins = new();
    private readonly HashSet<string> _dirtyImages = new();
    private readonly DispatcherTimer _save;
    private readonly Action<BitmapSource> _copy;
    private readonly Action<BitmapSource, Window?> _saveAs;

    public bool Hidden { get; private set; }
    public int Count => _pins.Count;

    /// <summary>Reads the text in a pin (its menu, or T over it).</summary>
    public Action<BitmapSource>? Recognize { get; init; }

    public PinManager(Action<BitmapSource> copy, Action<BitmapSource, Window?> saveAs)
    {
        _copy = copy;
        _saveAs = saveAs;
        _save = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _save.Tick += (_, _) => { _save.Stop(); SaveNow(); };
    }

    /// <summary>Pins an image with its top-left corner at <paramref name="at"/> (physical pixels).</summary>
    public void Add(BitmapSource image, PixelRect at)
    {
        if (Hidden) ShowAll(); // a new pin while the others are hidden: bring everything back
        var pin = Create(Guid.NewGuid().ToString("N"), image, at, 1, 1);
        _dirtyImages.Add(pin.Id);
        pin.FadeIn();
        ScheduleSave();
    }

    private PinWindow Create(string id, BitmapSource image, PixelRect at, double zoom, double opacity)
    {
        PinWindow? pin = null;
        pin = new PinWindow(id, image, at, zoom, opacity, () => OnChanged(pin!), _copy, _saveAs, Recognize);
        pin.Closed += (_, _) =>
        {
            _pins.Remove(pin);
            _dirtyImages.Remove(id);
            try { File.Delete(ImagePath(id)); } catch (IOException) { }
            ScheduleSave();
        };
        _pins.Add(pin);
        return pin;
    }

    private void OnChanged(PinWindow pin)
    {
        if (!_pins.Contains(pin)) return;
        _dirtyImages.Add(pin.Id); // marks may have changed; positions are cheap to rewrite anyway
        ScheduleSave();
    }

    /// <summary>The pin hotkey as the user set it ("" when it is off), for hints.</summary>
    public Func<string> PinHotkey { get; set; } = () => "";

    /// <summary>The hide / show hotkey: hide every pin, or show them all again.</summary>
    public void ToggleAll()
    {
        if (_pins.Count == 0)
        {
            Toast.Show(Loc.T("No pinned images"), PinHotkey() is { Length: > 0 } key
                ? Loc.F("Press {0} and choose an area to pin it to the screen.", key)
                : Loc.T("Choose “Pin an area of the screen” in the launcher to pin part of the screen."));
            return;
        }
        if (Hidden) ShowAll(); else HideAll();
    }

    public void HideAll()
    {
        Hidden = true;
        for (int i = 0; i < _pins.Count; i++) _pins[i].FadeOut(i * 18);
        ScheduleSave();
    }

    public void ShowAll()
    {
        Hidden = false;
        for (int i = 0; i < _pins.Count; i++) _pins[i].FadeIn(i * 25);
        ScheduleSave();
    }

    public void CloseAll()
    {
        foreach (var pin in _pins.ToList()) pin.Close();
    }

    private void ScheduleSave()
    {
        _save.Stop();
        _save.Start();
    }

    private static string ImagePath(string id) => Path.Combine(Folder, id + ".png");

    public void SaveNow()
    {
        _save.Stop();
        try
        {
            Directory.CreateDirectory(Folder);
            foreach (var pin in _pins.Where(p => _dirtyImages.Contains(p.Id)))
                File.WriteAllBytes(ImagePath(pin.Id), ScreenshotService.Encode(pin.Composed, jpeg: false, 100));
            _dirtyImages.Clear();
            var all = new SavedAll(Hidden, _pins.Select(p => { var (x, y) = p.ImagePosition; return new Saved(p.Id, x, y, p.Zoom, p.PinOpacity); }).ToList());
            string tmp = IndexFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, JsonOptions));
            File.Move(tmp, IndexFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warning(ex, "Could not save the pinned images"); }
    }

    /// <summary>Brings back the pins of the last session (still hidden if they were hidden).</summary>
    public void Restore()
    {
        try
        {
            if (!File.Exists(IndexFile)) return;
            var all = JsonSerializer.Deserialize<SavedAll>(File.ReadAllText(IndexFile), JsonOptions);
            if (all == null) return;
            Hidden = all.Hidden;
            int shown = 0;
            foreach (var s in all.Pins)
            {
                string path = ImagePath(s.Id);
                if (!File.Exists(path)) continue;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                var pin = Create(s.Id, bmp, new PixelRect(s.X, s.Y, bmp.PixelWidth, bmp.PixelHeight), s.Zoom, s.Opacity);
                if (!Hidden) pin.FadeIn(shown++ * 25);
            }
            Log.Information("Restored {Count} pinned image(s){Hidden}", _pins.Count, Hidden ? " (hidden)" : "");
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not restore the pinned images");
        }
    }
}
