using System.IO;
using System.Text.RegularExpressions;
using ChaoDesk.Host.Input;

namespace ChaoDesk.Host.Panel;

/// <summary>Validates action data without starting a process or sending input.</summary>
public static class PanelCustomAction
{
    public const string Shortcut = "shortcut";
    public const string Program = "program";
    public const string FileTarget = "file";
    public const string FolderTarget = "folder";
    public const string Url = "url";
    public const string Keys = "keys";
    public const string Text = "text";
    public const string Script = "script";
    public const string Command = "command";

    public static bool TryNormalize(PanelItem source, out PanelItem normalized, out string error,
        bool requireExistingPaths = false)
    {
        normalized = CopyForLoad(source);
        error = "";
        if (normalized.ActionId.Length != 0) return Fail("BuiltInAction", out error);
        string kind = normalized.Kind;
        if (kind is not (Shortcut or Program or FileTarget or FolderTarget or Url or Keys or Text or Script or Command))
            return Fail("InvalidKind", out error);
        if (string.IsNullOrWhiteSpace(normalized.Target)) return Fail("EmptyTarget", out error);
        if (normalized.Target.Contains('\0')) return Fail("InvalidTarget", out error);

        if (kind == Url || kind == Shortcut && LooksLikeUrl(normalized.Target))
        {
            if (!TryWebUrl(normalized.Target, out string target)) return Fail("InvalidUrl", out error);
            normalized.Target = target;
        }
        else if (kind is Program or Shortcut or FileTarget or FolderTarget)
        {
            if (!TryPath(normalized.Target, out string target)) return Fail("InvalidPath", out error);
            if (requireExistingPaths)
            {
                if (kind == FileTarget && !File.Exists(target)) return Fail("FileNotFound", out error);
                if (kind == FolderTarget && !Directory.Exists(target)) return Fail("FolderNotFound", out error);
                if (kind is Program or Shortcut && !File.Exists(target) && !Directory.Exists(target))
                    return Fail("PathNotFound", out error);
            }
            normalized.Target = target;
        }
        else if (kind == Keys)
        {
            if (!PanelKeySequence.TryParse(normalized.Target, out var chord, out _))
                return Fail("InvalidKeys", out error);
            normalized.Target = chord!.DisplayText;
        }
        else if (kind == Script && normalized.Engine is not ("powershell" or "cmd"))
            return Fail("InvalidScriptEngine", out error);

        if (kind == Program)
        {
            if (normalized.Arguments.IndexOfAny(['\0', '\r', '\n']) >= 0)
                return Fail("InvalidArguments", out error);
        }
        else normalized.Arguments = "";

        if (kind is Program or Script or Command)
        {
            if (!string.IsNullOrWhiteSpace(normalized.WorkingDirectory))
            {
                if (!TryPath(normalized.WorkingDirectory, out string directory))
                    return Fail("InvalidWorkingDirectory", out error);
                if (requireExistingPaths && !Directory.Exists(directory))
                    return Fail("WorkingDirectoryNotFound", out error);
                normalized.WorkingDirectory = directory;
            }
            else normalized.WorkingDirectory = "";
        }
        else normalized.WorkingDirectory = "";

        if (kind == Command) normalized.Engine = "cmd";
        else if (kind is FileTarget or FolderTarget) normalized.Engine = "";
        else if (kind != Script) normalized.Engine = "powershell";
        if (normalized.Name.Length == 0) normalized.Name = ShortName(DefaultName(normalized));
        return true;
    }

    // Loading is deliberately tolerant. A disconnected drive, unavailable app,
    // or future action kind must not erase a user's ordered saved action.
    internal static PanelItem CopyForLoad(PanelItem source)
    {
        string kind = string.IsNullOrWhiteSpace(source.Kind) ? Shortcut : source.Kind.Trim().ToLowerInvariant();
        return new PanelItem
        {
            Id = source.Id ?? "",
            ActionId = source.ActionId ?? "",
            Kind = kind,
            Name = ShortName(source.Name ?? ""),
            Target = kind is Text or Script or Command ? source.Target ?? "" : (source.Target ?? "").Trim(),
            Arguments = kind is FileTarget or FolderTarget or Command ? "" : source.Arguments ?? "",
            WorkingDirectory = kind is FileTarget or FolderTarget ? "" : source.WorkingDirectory ?? "",
            Engine = kind == Command ? "cmd" : kind is FileTarget or FolderTarget ? "" : (source.Engine ?? "").Trim().ToLowerInvariant()
        };
    }

    private static bool LooksLikeUrl(string target) =>
        !target.Contains('\\') && !Path.IsPathFullyQualified(target) &&
        (target.Contains('.') || target.Contains(':') || target.StartsWith("localhost", StringComparison.OrdinalIgnoreCase));

    private static bool TryWebUrl(string target, out string normalized)
    {
        normalized = Unquote(target);
        if (normalized.Any(char.IsWhiteSpace) || normalized.Contains('\\')) return false;
        bool hasScheme = Regex.IsMatch(normalized, @"^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool hostWithPort = Regex.IsMatch(normalized, @"^[^/:?#\s]+:\d+(?:[/?#]|$)", RegexOptions.CultureInvariant);
        if (!hasScheme || hostWithPort) normalized = "https://" + normalized;
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && uri.Host.Length > 0 &&
            uri.HostNameType != UriHostNameType.Unknown;
    }

    private static bool TryPath(string value, out string normalized)
    {
        normalized = Environment.ExpandEnvironmentVariables(Unquote(value));
        if (normalized.Length == 0 || normalized.Any(char.IsControl) ||
            normalized.IndexOfAny(['"', '<', '>', '|', '*', '?']) >= 0 ||
            !Path.IsPathFullyQualified(normalized)) return false;
        // Reject an alternate stream or URI disguised as a local path. A drive
        // letter is the only colon that belongs in an ordinary Windows path.
        int allowedColon = normalized.Length > 1 && normalized[1] == ':' ? 1 : -1;
        if (normalized.Where((c, i) => c == ':' && i != allowedColon).Any()) return false;
        try { normalized = Path.GetFullPath(normalized); return true; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }

    private static string DefaultName(PanelItem item)
    {
        if (item.Kind == Url || item.Kind == Shortcut && Uri.TryCreate(item.Target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return new Uri(item.Target).Host;
        if (item.Kind == Keys) return item.Target;
        if (item.Kind == Script) return item.Engine == "cmd" ? "Command script" : "PowerShell script";
        if (item.Kind is Text or Command) return item.Target.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? (item.Kind == Command ? "Command" : "Text");
        string path = item.Target.TrimEnd('\\', '/');
        string name = item.Kind == FolderTarget ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        return name.Length == 0 ? item.Target : name;
    }

    private static string ShortName(string name)
    { name = name.Trim(); return name[..Math.Min(name.Length, 32)]; }

    private static bool Fail(string code, out string error) { error = code; return false; }
}
