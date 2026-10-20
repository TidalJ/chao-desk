using System.IO;
using System.Text;

namespace ChaoDesk.Host.Preview;

/// <summary>Recognises and decodes text files for previewing (pure; unit-tested).</summary>
public static class TextFiles
{
    public const int MaxBytes = 4 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".ini", ".cfg", ".conf", ".config", ".json", ".jsonc", ".xml", ".xaml", ".yaml", ".yml", ".toml",
        ".csv", ".tsv", ".cs", ".csproj", ".sln", ".props", ".targets", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".css", ".scss", ".less",
        ".py", ".rb", ".go", ".rs", ".java", ".kt", ".swift", ".c", ".h", ".cpp", ".hpp", ".cc", ".m", ".php", ".lua", ".sql", ".sh", ".bash", ".zsh",
        ".ps1", ".psm1", ".bat", ".cmd", ".reg", ".gitignore", ".gitattributes", ".editorconfig", ".env", ".srt", ".ass", ".lrc", ".iss", ".nfo", ".properties", ".gradle", ".dockerfile",
    };

    static TextFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // GBK and friends

    public static bool HasTextExtension(string path) => Extensions.Contains(Path.GetExtension(path)) || Path.GetFileName(path) is "Dockerfile" or "Makefile" or "LICENSE" or "README";

    /// <summary>Looks like text: no NUL bytes in the first 8 KB (unless it is UTF-16 with a BOM).</summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF))) return true;
        return head[..Math.Min(head.Length, 8192)].IndexOf((byte)0) < 0;
    }

    /// <summary>Decodes with the BOM's encoding, else UTF-8 if valid, else the system ANSI code page (GBK on Chinese Windows).</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes);
        }
    }

    /// <summary>Reads up to <see cref="MaxBytes"/>; null if the file is not text.</summary>
    public static string? Read(string path, out bool truncated)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        truncated = fs.Length > MaxBytes;
        var buffer = new byte[Math.Min(fs.Length, MaxBytes)];
        fs.ReadExactly(buffer);
        if (!LooksLikeText(buffer)) return null;
        if (truncated)
        {
            // Do not cut a UTF-8 character in half (that would make the whole file look like ANSI).
            int end = buffer.Length;
            while (end > 0 && end > buffer.Length - 4 && (buffer[end - 1] & 0xC0) == 0x80) end--;
            if (end > 0 && buffer[end - 1] >= 0xC0) end--;
            buffer = buffer[..end];
        }
        return Decode(buffer);
    }
}
