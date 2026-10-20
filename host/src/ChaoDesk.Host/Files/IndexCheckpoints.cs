using System.Globalization;
using System.IO;

namespace ChaoDesk.Host.Files;

/// <summary>
/// The journal position each volume of the saved index was written at (<c>index\checkpoints.txt</c>, one line
/// per drive: letter, journal id, USN). The app writes it together with the index; the elevated helper reads it at
/// start to resume instead of re-reading the whole drive. It is plain data that the helper validates against the
/// live journal (same journal id, USN inside the journal's range), so a bad file only costs a full re-read.
/// </summary>
public static class IndexCheckpoints
{
    public readonly record struct Entry(ulong JournalId, long Usn);

    public static string Folder => Path.Combine(AppPaths.DataRoot, "index");
    public static string DefaultPath => Path.Combine(Folder, "checkpoints.txt");

    public static IReadOnlyDictionary<char, Entry> Read(string path)
    {
        var result = new Dictionary<char, Entry>();
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return result;
            foreach (string line in File.ReadAllLines(path))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || parts[0].Length != 1 || !char.IsAsciiLetter(parts[0][0])) continue;
                if (!ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong journal)) continue;
                if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long usn) || usn < 0) continue;
                result[char.ToUpperInvariant(parts[0][0])] = new Entry(journal, usn);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    public static void Write(string path, IReadOnlyDictionary<char, Entry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllLines(tmp, entries.OrderBy(e => e.Key).Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Key} {e.Value.JournalId} {e.Value.Usn}")));
        File.Move(tmp, path, overwrite: true);
    }

    public static void Delete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
