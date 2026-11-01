namespace ChaoDesk.Host.Panel;

public sealed class PanelItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ActionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string Kind { get; set; } = PanelCustomAction.Shortcut;
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string Engine { get; set; } = "powershell";
}

/// <summary>Persistent ordered slots. Removing a shortcut never removes its target.</summary>
public sealed class PanelLayout
{
    public const int Capacity = 15;
    public const int Columns = 3;
    public const double Width = 324;
    private readonly List<PanelItem> _items;
    private readonly Action<List<PanelItem>> _save;
    public IReadOnlyList<PanelItem> Items => _items;
    public bool IsFull => _items.Count >= Capacity;
    public int HomeTileCount => _items.Count;
    public int Rows => Math.Max(1, (HomeTileCount + Columns - 1) / Columns);
    public double HomeHeight => 82 + Rows * 82;

    public PanelLayout(IEnumerable<PanelItem>? items = null, Action<List<PanelItem>>? save = null)
    { _items = Normalize(items); _save = save ?? (_ => { }); }

    public static List<PanelItem> Defaults() => new[] { "restore", "clipboard", "screenshot", "ocr", "pin" }
        .Select(id => new PanelItem { ActionId = id }).ToList();

    public static List<PanelItem> Normalize(IEnumerable<PanelItem>? items)
    {
        if (items == null) return Defaults();
        var result = new List<PanelItem>();
        foreach (var item in items)
        {
            if (item == null || result.Count == Capacity) continue;
            string action = item.ActionId ?? "";
            if (action.Length > 0 && (PanelAction.Find(action) == null || result.Any(x => x.ActionId == action))) continue;
            if (action.Length == 0 && (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Target))) continue;
            string id = item.Id;
            if (string.IsNullOrWhiteSpace(id) || result.Any(x => x.Id == id)) id = Guid.NewGuid().ToString("N");
            var copy = PanelCustomAction.CopyForLoad(item);
            copy.Id = id;
            result.Add(copy);
        }
        return result;
    }

    public bool AddAction(string action)
    {
        if (IsFull || PanelAction.Find(action) == null || _items.Any(x => x.ActionId == action)) return false;
        _items.Add(new PanelItem { ActionId = action }); Changed(); return true;
    }

    public bool SaveShortcut(string? id, string name, string target)
        => SaveCustom(id, name, PanelCustomAction.Shortcut, target);

    public bool SaveCustom(string? id, string name, string kind, string target,
        string arguments = "", string workingDirectory = "", string engine = "powershell")
        => SaveCustom(id, name, kind, target, arguments, workingDirectory, engine, out _);

    public bool SaveCustom(string? id, string name, string kind, string target,
        string arguments, string workingDirectory, string engine, out string error)
    {
        var existing = id == null ? null : _items.FirstOrDefault(x => x.Id == id && x.ActionId.Length == 0);
        error = "";
        if (id != null && existing == null) { error = "NotFound"; return false; }
        if (existing == null && IsFull) { error = "PanelFull"; return false; }
        var draft = new PanelItem
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = name, Kind = kind, Target = target,
            Arguments = arguments, WorkingDirectory = workingDirectory, Engine = engine
        };
        if (!PanelCustomAction.TryNormalize(draft, out var item, out error, requireExistingPaths: true)) return false;
        if (existing == null) _items.Add(item);
        else _items[_items.IndexOf(existing)] = item;
        Changed(); return true;
    }

    public bool Remove(string id)
    { if (_items.RemoveAll(x => x.Id == id) == 0) return false; Changed(); return true; }

    public bool Move(string id, int to)
    {
        int from = _items.FindIndex(x => x.Id == id);
        if (from < 0 || to < 0 || to >= _items.Count || from == to) return false;
        var item = _items[from]; _items.RemoveAt(from); _items.Insert(to, item); Changed(); return true;
    }

    private void Changed() => _save(_items.ToList());
}
