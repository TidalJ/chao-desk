using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChaoDesk.Host.Library;

public enum PropertyType { Slider, Bool, Color, Combo, Text }

/// <summary>One user-editable property from wallpaper.json (docs/wallpaper-format.md).</summary>
public sealed class PropertyDefinition
{
    public required string Key { get; init; }
    public required PropertyType Type { get; init; }
    public required string Label { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double? Step { get; init; }
    public IReadOnlyList<string> Options { get; init; } = [];
    public int? MaxLength { get; init; }
    public required JsonNode Default { get; init; }

    /// <summary>Coerce a stored value to this property's type; invalid values return the default.</summary>
    public JsonNode Coerce(JsonNode? value)
    {
        try
        {
            switch (Type)
            {
                case PropertyType.Slider:
                    if (value is JsonValue v && TryNumber(v, out double n) && double.IsFinite(n))
                    {
                        n = Math.Clamp(n, Min, Max);
                        if (Step is > 0) n = Math.Clamp(Min + Math.Round((n - Min) / Step.Value) * Step.Value, Min, Max);
                        return JsonValue.Create(Math.Round(n, 6))!;
                    }
                    break;
                case PropertyType.Bool:
                    if (value is JsonValue b && b.TryGetValue(out bool bv)) return JsonValue.Create(bv)!;
                    break;
                case PropertyType.Color:
                    if (value is JsonValue c && c.TryGetValue(out string? cs) && WallpaperManifest.IsHexColor(cs)) return JsonValue.Create(cs)!;
                    break;
                case PropertyType.Combo:
                    if (value is JsonValue o && o.TryGetValue(out string? os) && Options.Contains(os)) return JsonValue.Create(os)!;
                    break;
                case PropertyType.Text:
                    if (value is JsonValue t && t.TryGetValue(out string? ts))
                        return JsonValue.Create(MaxLength is > 0 && ts.Length > MaxLength ? ts[..MaxLength.Value] : ts)!;
                    break;
            }
        }
        catch (InvalidOperationException) { /* wrong JSON kind */ }
        return Default.DeepClone();
    }

    private static bool TryNumber(JsonValue v, out double n)
    {
        if (v.TryGetValue(out n)) return true;
        if (v.TryGetValue(out int i)) { n = i; return true; }
        if (v.TryGetValue(out long l)) { n = l; return true; }
        if (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return true;
        n = 0;
        return false;
    }
}

/// <summary>Parsed and validated wallpaper.json. Parsing never throws; problems are listed in <see cref="Errors"/>.</summary>
public sealed partial class WallpaperManifest
{
    public const string FileName = "wallpaper.json";

    public string Id { get; private init; } = "unknown";
    public string Name { get; private init; } = "Unknown";
    public string Author { get; private init; } = "";
    public string Version { get; private init; } = "";
    public string Entry { get; private init; } = "index.html";
    public string Preview { get; private init; } = "preview.png";
    public string Background { get; private init; } = "#000000";
    /// <summary>Diagnostic wallpapers are only listed in Debug builds.</summary>
    public bool Diagnostic { get; private init; }
    public IReadOnlyList<PropertyDefinition> Properties { get; private init; } = [];
    public IReadOnlyList<string> Errors { get; private init; } = [];

    /// <summary>True if the package can be loaded (id valid, entry present). Property problems are non-fatal.</summary>
    public bool IsUsable { get; private init; }

    [GeneratedRegex("^#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})$")]
    private static partial Regex HexColorRegex();

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$")]
    private static partial Regex IdRegex();

    public static bool IsHexColor(string? s) => s != null && HexColorRegex().IsMatch(s);

    public static WallpaperManifest Load(string folder)
    {
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
            return new WallpaperManifest { Errors = [$"{FileName} not found"], IsUsable = false };
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) { return new WallpaperManifest { Errors = [$"cannot read {FileName}: {ex.Message}"] }; }
        var m = Parse(json);
        if (m.IsUsable && !File.Exists(Path.Combine(folder, m.Entry)))
            return m.With(errors: [.. m.Errors, $"entry page \"{m.Entry}\" not found"], usable: false);
        return m;
    }

    public static WallpaperManifest Parse(string json)
    {
        var errors = new List<string>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            return new WallpaperManifest { Errors = [$"invalid JSON: {ex.Message}"] };
        }
        if (root is not JsonObject obj)
            return new WallpaperManifest { Errors = ["manifest must be a JSON object"] };

        string? id = Str(obj, "id");
        bool idOk = id != null && IdRegex().IsMatch(id);
        if (!idOk) errors.Add($"\"id\" must be letters, digits, '.', '_' or '-' (got {obj["id"]?.ToJsonString() ?? "nothing"})");
        string? name = Str(obj, "name");
        if (string.IsNullOrWhiteSpace(name)) errors.Add("\"name\" is required");
        string entry = Str(obj, "entry") is { Length: > 0 } e ? e : "index.html";
        if (entry.Contains("..") || Path.IsPathRooted(entry)) { errors.Add("\"entry\" must be a relative path inside the package"); entry = "index.html"; }

        var props = new List<PropertyDefinition>();
        if (obj["properties"] is JsonObject pObj)
        {
            foreach (var (key, def) in pObj)
                if (ParseProperty(key, def, errors) is { } p) props.Add(p);
        }
        else if (obj["properties"] != null)
        {
            errors.Add("\"properties\" must be an object");
        }

        string background = Str(obj, "background") is { } bg && IsHexColor(bg) ? bg
            : props.FirstOrDefault(p => p.Type == PropertyType.Color)?.Default.GetValue<string>() ?? "#000000";

        return new WallpaperManifest
        {
            Id = idOk ? id! : "unknown",
            Name = string.IsNullOrWhiteSpace(name) ? (idOk ? id! : "Unknown") : name!,
            Author = Str(obj, "author") ?? "",
            Version = Str(obj, "version") ?? "",
            Entry = entry,
            Preview = Str(obj, "preview") is { Length: > 0 } pv && !pv.Contains("..") ? pv : "preview.png",
            Background = background,
            Diagnostic = obj["diagnostic"] is JsonValue dv && dv.TryGetValue(out bool d) && d,
            Properties = props,
            Errors = errors,
            IsUsable = idOk,
        };
    }

    private WallpaperManifest With(IReadOnlyList<string> errors, bool usable) => new()
    {
        Id = Id, Name = Name, Author = Author, Version = Version, Entry = Entry, Preview = Preview,
        Background = Background, Diagnostic = Diagnostic, Properties = Properties, Errors = errors, IsUsable = usable,
    };

    private static PropertyDefinition? ParseProperty(string key, JsonNode? def, List<string> errors)
    {
        string where = $"properties.{key}";
        if (def is not JsonObject o) { errors.Add($"{where} must be an object"); return null; }
        string label = Str(o, "label") ?? key;
        switch (Str(o, "type"))
        {
            case "slider":
            {
                double? min = Num(o, "min"), max = Num(o, "max");
                if (min == null || max == null || min >= max) { errors.Add($"{where}: slider needs numeric min < max"); return null; }
                double? step = Num(o, "step") is > 0 ? Num(o, "step") : null;
                double d = Math.Clamp(Num(o, "default") ?? min.Value, min.Value, max.Value);
                return new PropertyDefinition { Key = key, Type = PropertyType.Slider, Label = label, Min = min.Value, Max = max.Value, Step = step, Default = JsonValue.Create(d)! };
            }
            case "bool":
                return new PropertyDefinition { Key = key, Type = PropertyType.Bool, Label = label,
                    Default = JsonValue.Create(o["default"] is JsonValue b && b.TryGetValue(out bool bv) && bv)! };
            case "color":
            {
                string? d = Str(o, "default");
                if (!IsHexColor(d)) { errors.Add($"{where}: default must be a #rrggbb colour"); d = "#000000"; }
                return new PropertyDefinition { Key = key, Type = PropertyType.Color, Label = label, Default = JsonValue.Create(d)! };
            }
            case "combo":
            {
                var options = (o["options"] as JsonArray)?.Select(x => x is JsonValue v && v.TryGetValue(out string? s) ? s : null).ToList();
                if (options == null || options.Count == 0 || options.Any(x => x == null)) { errors.Add($"{where}: combo needs a non-empty \"options\" string array"); return null; }
                string d = Str(o, "default") is { } s2 && options.Contains(s2) ? s2 : options[0]!;
                return new PropertyDefinition { Key = key, Type = PropertyType.Combo, Label = label, Options = options!, Default = JsonValue.Create(d)! };
            }
            case "text":
            {
                double? mlv = Num(o, "maxLength");
                int? maxLen = mlv is > 0 ? (int)mlv.Value : null;
                return new PropertyDefinition { Key = key, Type = PropertyType.Text, Label = label, MaxLength = maxLen, Default = JsonValue.Create(Str(o, "default") ?? "")! };
            }
            default:
                errors.Add($"{where}: unknown type {o["type"]?.ToJsonString() ?? "(missing)"} (expected slider, bool, color, combo or text)");
                return null;
        }
    }

    /// <summary>Defaults overlaid with stored values, all coerced. Unknown keys are dropped.</summary>
    public JsonObject MergeProperties(IReadOnlyDictionary<string, JsonNode?>? stored)
    {
        var result = new JsonObject();
        foreach (var p in Properties)
        {
            JsonNode? v = null;
            stored?.TryGetValue(p.Key, out v);
            result[p.Key] = v == null ? p.Default.DeepClone() : p.Coerce(v);
        }
        return result;
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static double? Num(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l)) return l;
        return null;
    }
}
