using System.IO;

namespace ChaoDesk.Host.Settings;

/// <summary>One widget on the wallpaper card, in the user's order.</summary>
public sealed class WidgetItem
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; }
}

/// <summary>A website, folder or program the shortcuts widget opens.</summary>
public sealed class ShortcutItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";

    /// <summary>"web", "folder" or "app" — only for the icon on the card.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Kind => WidgetCatalog.IsWebUrl(Target) ? "web" : Directory.Exists(Target) ? "folder" : "app";
}

public static class WidgetCatalog
{
    public sealed record Info(string Id, string Name, string Description, bool DefaultOn);

    /// <summary>Every widget the wallpaper knows, in the default order.</summary>
    public static readonly Info[] Catalog =
    [
        new("calendar", "Calendar", "Date, lunar date, festivals and the official 休/班 schedule. Click it for the month.", true),
        new("rate", "AUD exchange rate", "Bank of China quotation for the Australian dollar: selling and buying price.", true),
        new("weather", "Weather", "Current weather and a 4-day forecast (city: General → Weather city).", true),
        new("worldclock", "World clock", "Other cities' time (cities: Wallpaper → World clock cities).", true),
        new("clock", "Clock", "A large clock. Off by default: the taskbar already shows the time.", false),
        new("tasks", "Tasks", "Your task list (Tasks page). Click a task on the desktop to tick it off.", false),
        new("system", "Computer status", "CPU, memory, battery and free disk space, updated every few seconds.", false),
        new("shortcuts", "Shortcuts", "Websites, folders and programs you open often, one click away.", false),
    ];

    public static readonly string[] Layouts = ["stack", "tabs"];

    /// <summary>Most widgets on one stacked page; 0 = as many as fit on the screen.</summary>
    public static readonly int[] PerPageOptions = [0, 2, 3, 4, 5, 6];

    /// <summary>"peek": a see-through clock until clicked; "open": the card is always shown.</summary>
    public static readonly string[] Displays = ["peek", "open"];

    public static Info? Find(string id) => Catalog.FirstOrDefault(w => w.Id == id);

    /// <summary>Keeps the user's order, drops unknown or duplicate ids, appends new widgets (with their default).</summary>
    public static List<WidgetItem> Normalize(List<WidgetItem>? items)
    {
        var list = new List<WidgetItem>();
        foreach (var w in items ?? [])
            if (w != null && Find(w.Id) != null && list.All(x => x.Id != w.Id)) list.Add(new WidgetItem { Id = w.Id, Enabled = w.Enabled });
        foreach (var info in Catalog)
            if (list.All(x => x.Id != info.Id)) list.Add(new WidgetItem { Id = info.Id, Enabled = info.DefaultOn });
        return list;
    }

    public static bool IsWebUrl(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>A shortcut target must be a web address or an existing file or folder.</summary>
    public static bool IsValidTarget(string target) =>
        IsWebUrl(target) || (Path.IsPathFullyQualified(target) && (File.Exists(target) || Directory.Exists(target)));
}
