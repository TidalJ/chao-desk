using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace ChaoDesk.Host.Clips;

public enum ClipKind { Text, Image, Files }

/// <summary>One clipboard entry. Images live in their own PNG file next to history.json.</summary>
public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ClipKind Kind { get; set; }

    /// <summary>The text, or the file paths one per line.</summary>
    public string Text { get; set; } = "";

    public string? ImageFile { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public string Hash { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public bool Pinned { get; set; }

    /// <summary>The app it was copied from, e.g. "Microsoft Edge".</summary>
    public string Source { get; set; } = "";

    [JsonIgnore] public string[] Files => Kind == ClipKind.Files ? Text.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];

    public static string HashOf(ClipKind kind, ReadOnlySpan<byte> content)
    {
        Span<byte> hash = stackalloc byte[32];
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData([(byte)kind]);
        sha.AppendData(content);
        sha.GetHashAndReset(hash);
        return Convert.ToHexString(hash[..16]);
    }

    public static string HashOf(ClipKind kind, string text) => HashOf(kind, Encoding.UTF8.GetBytes(text));
}

/// <summary>
/// The clipboard history: newest first, duplicates move to the top, pinned items are never trimmed.
/// Persisted to <c>%LOCALAPPDATA%\ChaoDesk\clipboard\history.json</c> plus one PNG per image. UI thread only.
/// </summary>
public sealed class ClipboardHistory
{
    public const int MaxTextLength = 1_000_000;
    public static readonly int[] AllowedSizes = [100, 300, 500, 1000, 2000];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    private readonly string _folder;
    private readonly List<ClipItem> _items = new();

    public int MaxItems { get; set; }
    public IReadOnlyList<ClipItem> Items => _items;
    public event Action? Changed;

    public ClipboardHistory(string folder, int maxItems)
    {
        _folder = folder;
        MaxItems = maxItems;
    }

    private string IndexFile => Path.Combine(_folder, "history.json");
    private string ImagesFolder => Path.Combine(_folder, "images");
    public string ImagePath(ClipItem item) => Path.Combine(ImagesFolder, item.ImageFile ?? "");

    public void Load()
    {
        try
        {
            if (!File.Exists(IndexFile)) return;
            var items = JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(IndexFile), JsonOptions) ?? new();
            _items.Clear();
            _items.AddRange(items.Where(i => i.Kind != ClipKind.Image || File.Exists(ImagePath(i))));
            DeleteOrphanImages();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the clipboard history; starting empty");
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            string tmp = IndexFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, JsonOptions));
            File.Move(tmp, IndexFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not save the clipboard history");
        }
    }

    /// <summary>
    /// Adds an entry (or moves its duplicate to the top). <paramref name="png"/> is the encoded image for image
    /// entries. Returns the entry now at the top.
    /// </summary>
    public ClipItem Add(ClipItem item, byte[]? png = null)
    {
        var existing = _items.FirstOrDefault(i => i.Hash == item.Hash && i.Kind == item.Kind);
        if (existing != null)
        {
            _items.Remove(existing);
            existing.Created = DateTime.Now;
            if (existing.Source.Length == 0) existing.Source = item.Source;
            _items.Insert(0, existing);
            Changed?.Invoke();
            return existing;
        }
        if (item.Kind == ClipKind.Image && png != null)
        {
            Directory.CreateDirectory(ImagesFolder);
            item.ImageFile = item.Id + ".png";
            File.WriteAllBytes(ImagePath(item), png);
        }
        if (item.Text.Length > MaxTextLength) item.Text = item.Text[..MaxTextLength];
        _items.Insert(0, item);
        Trim();
        Changed?.Invoke();
        return item;
    }

    /// <summary>Moves an entry to the top (it was just pasted).</summary>
    public void Touch(string id)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item == null || _items[0] == item) return;
        _items.Remove(item);
        item.Created = DateTime.Now;
        _items.Insert(0, item);
        Changed?.Invoke();
    }

    public void TogglePin(string id)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item == null) return;
        item.Pinned = !item.Pinned;
        Trim();
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item == null) return;
        _items.Remove(item);
        DeleteImage(item);
        Changed?.Invoke();
    }

    /// <summary>Removes everything except pinned entries.</summary>
    public void ClearUnpinned()
    {
        foreach (var item in _items.Where(i => !i.Pinned).ToList())
        {
            _items.Remove(item);
            DeleteImage(item);
        }
        Changed?.Invoke();
    }

    /// <summary>Drops the oldest unpinned entries beyond <see cref="MaxItems"/>.</summary>
    public void Trim()
    {
        int unpinned = _items.Count(i => !i.Pinned);
        for (int i = _items.Count - 1; i >= 0 && unpinned > MaxItems; i--)
        {
            if (_items[i].Pinned) continue;
            DeleteImage(_items[i]);
            _items.RemoveAt(i);
            unpinned--;
        }
    }

    /// <summary>Entries matching <paramref name="query"/> (text, file names or source app; case-insensitive).</summary>
    public IEnumerable<ClipItem> Search(string query, ClipFilter filter)
    {
        IEnumerable<ClipItem> items = filter switch
        {
            ClipFilter.Pinned => _items.Where(i => i.Pinned),
            ClipFilter.Text => _items.Where(i => i.Kind == ClipKind.Text),
            ClipFilter.Images => _items.Where(i => i.Kind == ClipKind.Image),
            ClipFilter.Files => _items.Where(i => i.Kind == ClipKind.Files),
            _ => _items,
        };
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return terms.Length == 0 ? items : items.Where(i => terms.All(t =>
            i.Text.Contains(t, StringComparison.OrdinalIgnoreCase) || i.Source.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }

    private void DeleteImage(ClipItem item)
    {
        if (item.Kind != ClipKind.Image || item.ImageFile == null) return;
        try { File.Delete(ImagePath(item)); } catch (IOException) { /* in use; DeleteOrphanImages gets it next time */ }
    }

    private void DeleteOrphanImages()
    {
        if (!Directory.Exists(ImagesFolder)) return;
        var keep = _items.Where(i => i.ImageFile != null).Select(i => i.ImageFile!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(ImagesFolder, "*.png"))
            if (!keep.Contains(Path.GetFileName(file)))
                try { File.Delete(file); } catch (IOException) { }
    }
}

public enum ClipFilter { All, Pinned, Text, Images, Files }
