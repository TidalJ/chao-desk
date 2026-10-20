using System.IO;

namespace ChaoDesk.Host.Launcher;

/// <summary>
/// Toneless pinyin readings for CJK Unified Ideographs, most common reading first (embedded pinyin.txt,
/// generated from Unicode's Unihan database).
/// </summary>
public static class Pinyin
{
    private static readonly Lazy<Dictionary<char, string[]>> Table = new(Load);

    public static bool IsHan(char c) => c is >= '一' and <= '鿿';

    /// <summary>The readings of <paramref name="c"/>, or an empty array.</summary>
    public static string[] Of(char c) => IsHan(c) && Table.Value.TryGetValue(c, out var r) ? r : [];

    private static Dictionary<char, string[]> Load()
    {
        var table = new Dictionary<char, string[]>(21000);
        using var s = typeof(Pinyin).Assembly.GetManifestResourceStream("ChaoDesk.Host.Launcher.pinyin.txt");
        if (s == null) return table;
        using var reader = new StreamReader(s);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 2) continue;
            table[line[0]] = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        return table;
    }
}
