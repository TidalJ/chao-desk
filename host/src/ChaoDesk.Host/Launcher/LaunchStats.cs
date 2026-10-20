using System.IO;
using System.Text.Json;
using Serilog;

namespace ChaoDesk.Host.Launcher;

/// <summary>
/// What the user opens and what they typed to get there, so frequent choices rise to the top and a query that
/// once led to an app ("wx" → WeChat) leads there first next time. Stored in launcher.json.
/// </summary>
public sealed class LaunchStats
{
    public sealed class Entry
    {
        public int Count { get; set; }
        public DateTime Last { get; set; }
    }

    public Dictionary<string, Entry> Items { get; set; } = new();

    /// <summary>Query → the id chosen for it most recently.</summary>
    public Dictionary<string, string> Queries { get; set; } = new();

    /// <summary>An item the user pinned (Ctrl+P): shown first when the box is empty, and first when it matches.</summary>
    public sealed class Pin
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public LauncherItemKind Kind { get; set; }
        public string? Path { get; set; }
    }

    public List<Pin> Pins { get; set; } = new();

    public const int PinBoost = 300;
    public const int MaxPins = 30;

    public bool IsPinned(string id) => Pins.Any(p => p.Id == id);

    /// <summary>Pins or unpins; returns whether it is pinned now.</summary>
    public bool TogglePin(LauncherItem item)
    {
        int at = Pins.FindIndex(p => p.Id == item.Id);
        if (at >= 0) { Pins.RemoveAt(at); return false; }
        if (Pins.Count >= MaxPins) Pins.RemoveAt(Pins.Count - 1);
        Pins.Insert(0, new Pin { Id = item.Id, Title = item.Title, Kind = item.Kind, Path = item.Path });
        return true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Boost(string id, string query, DateTime now)
    {
        int boost = IsPinned(id) ? PinBoost : 0;
        if (Items.TryGetValue(id, out var e))
        {
            boost += Math.Min(e.Count, 30) * 8;
            double days = (now - e.Last).TotalDays;
            boost += days < 1 ? 40 : days < 7 ? 20 : 0;
        }
        if (query.Length > 0)
        {
            if (Queries.TryGetValue(query, out var chosen) && chosen == id) boost += 400;
            else if (Queries.Any(q => q.Value == id && q.Key.StartsWith(query, StringComparison.Ordinal))) boost += 150;
        }
        return boost;
    }

    public void Record(string id, string query, DateTime now)
    {
        if (!Items.TryGetValue(id, out var e)) Items[id] = e = new Entry();
        e.Count++;
        e.Last = now;
        if (query.Length is > 0 and <= 40) Queries[query] = id;
        if (Queries.Count > 2000)
            foreach (var key in Queries.Keys.Take(500).ToList()) Queries.Remove(key);
    }

    /// <summary>Removes one item from the history (its use count and the searches that picked it).</summary>
    public void Forget(string id)
    {
        Items.Remove(id);
        foreach (var key in Queries.Where(q => q.Value == id).Select(q => q.Key).ToList()) Queries.Remove(key);
    }

    /// <summary>Forgets every use and search; pins stay.</summary>
    public void ClearHistory()
    {
        Items.Clear();
        Queries.Clear();
    }

    /// <summary>The most used ids, for the launcher's empty state.</summary>
    public IEnumerable<string> Frequent(int count) =>
        Items.OrderByDescending(kv => Math.Min(kv.Value.Count, 30) + ((DateTime.Now - kv.Value.Last).TotalDays < 3 ? 5 : 0))
             .Select(kv => kv.Key).Take(count);

    public static LaunchStats Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<LaunchStats>(File.ReadAllText(path), JsonOptions) ?? new();
        }
        catch (Exception ex) { Log.Warning(ex, "Could not read launcher stats"); }
        return new LaunchStats();
    }

    public void Save(string path)
    {
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { Log.Warning(ex, "Could not save launcher stats"); }
    }
}
