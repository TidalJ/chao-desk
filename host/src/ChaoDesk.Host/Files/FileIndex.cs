using System.IO;
using System.Runtime;
using System.Text;
using System.Threading;

namespace ChaoDesk.Host.Files;

/// <summary>One search hit.</summary>
public readonly record struct FileHit(string Path, string Name, bool IsDirectory);

public sealed record FileSearchResult(IReadOnlyList<FileHit> Hits, int Total)
{
    public static readonly FileSearchResult None = new([], 0);
}

/// <summary>
/// Every file and folder name on the indexed NTFS volumes, kept the way Everything keeps it: no object per file.
/// All names sit in one UTF-8 byte buffer, with a few parallel arrays per entry (parent, name offset/length,
/// flags). A search scans that whole buffer with vectorised IndexOfAny per term and looks only at the entries where
/// the term occurs, so its cost hardly depends on the number of files. NTFS file reference numbers map to entries
/// through a plain array per volume indexed by MFT record number. About 50 bytes per file including its name.
///
/// Volumes are identified by drive letter index (C: = 2). Thread safety: writers take the write lock through
/// <see cref="BeginWrite"/>; searches take the read lock and scan in parallel.
/// </summary>
public sealed class FileIndex
{
    private const byte FlagDirectory = 1;
    private const byte FlagHidden = 2;
    private const byte FlagSystem = 4;
    private const byte FlagPruned = 0x40; // a skipped folder (node_modules, WinSxS): listed itself, its contents are not kept
    private const byte FlagDeleted = 0x80;

    /// <summary>MFT record number of a volume's root folder (the FAT scanner uses it too).</summary>
    private const int RootRecord = 5;

    private const uint FILE_ATTRIBUTE_HIDDEN = 0x2;
    private const uint FILE_ATTRIBUTE_SYSTEM = 0x4;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    /// <summary>Longest name we keep, in UTF-8 bytes (NTFS allows 255 UTF-16 units).</summary>
    private const int MaxNameBytes = 1024;

    private readonly ReaderWriterLockSlim _lock = new();
    private readonly bool[] _bulk = new bool[26];

    /// <summary>Per volume: MFT record number → entry + 1 (0 = none).</summary>
    private readonly int[]?[] _records = new int[26][];

    // Per entry. While a volume loads, _parent holds -2 - (parent record number) until EndVolume resolves it.
    private int[] _parent = new int[1024];
    private int[] _nameStart = new int[1024];
    private ushort[] _nameLen = new ushort[1024];
    private ushort[] _sequence = new ushort[1024];
    private byte[] _flags = new byte[1024];
    private byte[] _volume = new byte[1024];
    private int _count;
    private int _deleted;

    // Names (UTF-8) and, in buffer order, which entry each stored name belongs to (renames leave stale ones).
    private byte[] _names = new byte[16384];
    private int _namesUsed;
    private int _wastedBytes;
    private int[] _runStart = new int[1024];
    private int[] _runEntry = new int[1024];
    private int _runs;

    private HashSet<int> _excluded = new();
    private string[] _excludedPaths = [];
    private bool _skipJunk;

    /// <summary>Live (not deleted) entries.</summary>
    public int Count
    {
        get { _lock.EnterReadLock(); try { return _count - _deleted; } finally { _lock.ExitReadLock(); } }
    }

    /// <summary>Approximate memory held by the index, in bytes.</summary>
    public long MemoryBytes
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                long records = _records.Where(r => r != null).Sum(r => (long)r!.Length * 4);
                return (long)_parent.Length * (4 + 4 + 2 + 2 + 1 + 1) + _names.Length + (long)_runStart.Length * 8 + records;
            }
            finally { _lock.ExitReadLock(); }
        }
    }

    private static int RecordOf(long frn) => (int)Math.Min(frn & 0xFFFF_FFFF_FFFFL, int.MaxValue - 4);
    private static ushort SequenceOf(long frn) => (ushort)((ulong)frn >> 48);

    // ---- Writing -----------------------------------------------------------------------------------

    /// <summary>Holds the write lock until disposed. Batch many changes into one writer.</summary>
    public Writer BeginWrite()
    {
        _lock.EnterWriteLock();
        return new Writer(this);
    }

    public readonly struct Writer : IDisposable
    {
        private readonly FileIndex _index;
        internal Writer(FileIndex index) => _index = index;

        /// <summary>A volume starts loading: clear what we had for it. <paramref name="estimate"/> is its MFT record count.</summary>
        public void BeginVolume(int volume, long estimate) => _index.BeginVolumeCore(volume, estimate);
        public void EndVolume(int volume) => _index.EndVolumeCore(volume);
        public void Upsert(int volume, long frn, long parentFrn, uint attributes, ReadOnlySpan<char> name) => _index.UpsertCore(volume, frn, parentFrn, attributes, name);
        public void Delete(int volume, long frn) => _index.DeleteCore(volume, frn);
        public void ClearVolume(int volume) => _index.ClearVolumeCore(volume);

        public void Dispose()
        {
            _index.CompactIfWorthIt();
            _index._lock.ExitWriteLock();
        }
    }

    private void BeginVolumeCore(int volume, long estimate)
    {
        ClearVolumeCore(volume);
        _bulk[volume] = true;
        // The MFT record count sizes the record map exactly; entries are fewer (free records), so reserve part.
        _records[volume] = new int[(int)Math.Clamp(estimate + 1024, 1024, 64_000_000)];
        int reserve = (int)Math.Min(estimate / 2, 2_000_000);
        if (_count + reserve > _parent.Length) ResizeEntries(_count + reserve);
    }

    private void EndVolumeCore(int volume)
    {
        _bulk[volume] = false;
        var records = _records[volume];
        if (records == null) return;
        for (int i = 0; i < _count; i++)
        {
            if (_volume[i] != volume || _parent[i] > -2) continue;
            int rec = -2 - _parent[i];
            _parent[i] = rec < records.Length ? records[rec] - 1 : -1;
        }
        int pruned = Prune();
        if (!_bulk.Any(b => b))
        {
            if (pruned > 0) Compact();
            TrimExcess();
        }
        ResolveExcluded();
    }

    /// <summary>Shrinks the arrays to what is used plus room for a while of changes, and returns the slack to Windows.</summary>
    private void TrimExcess()
    {
        int entries = _count + _count / 16 + 1024;
        if (entries < _parent.Length) ResizeEntries(entries);
        int bytes = _namesUsed + _namesUsed / 16 + 16384;
        if (bytes < _names.Length) Array.Resize(ref _names, bytes);
        int runs = _runs + _runs / 16 + 1024;
        if (runs < _runStart.Length) ResizeRuns(runs);
        // Loading grew big arrays several times; compact the large object heap once so the memory really goes back.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true); // Aggressive also decommits the freed segments
    }

    private int[] RecordsFor(int volume, int record)
    {
        var map = _records[volume] ??= new int[1024];
        if (record >= map.Length)
        {
            Array.Resize(ref map, Math.Max(record + 1024, map.Length + map.Length / 2));
            _records[volume] = map;
        }
        return map;
    }

    private void UpsertCore(int volume, long frn, long parentFrn, uint attributes, ReadOnlySpan<char> name)
    {
        if (name.Length == 0) return;
        Span<byte> utf8 = stackalloc byte[MaxNameBytes];
        if (!Encoding.UTF8.TryGetBytes(name, utf8, out int length)) return;
        utf8 = utf8[..length];

        int record = RecordOf(frn);
        ushort sequence = SequenceOf(frn);
        var map = RecordsFor(volume, record);
        byte flags = (byte)(((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0 ? FlagDirectory : 0) |
                            ((attributes & FILE_ATTRIBUTE_HIDDEN) != 0 ? FlagHidden : 0) |
                            ((attributes & FILE_ATTRIBUTE_SYSTEM) != 0 ? FlagSystem : 0));
        int idx = map[record] - 1;
        if (idx >= 0 && _sequence[idx] != sequence)
        {
            // The MFT record was reused for another file: the old one is gone.
            MarkDeleted(idx);
            idx = -1;
        }
        int parentRecord = RecordOf(parentFrn);
        if (_skipJunk && !_bulk[volume])
        {
            // A live change inside a skipped folder (or under one whose entries were dropped): not kept.
            int p = parentRecord < map.Length ? map[parentRecord] - 1 : -1;
            if (p >= 0 ? (_flags[p] & (FlagPruned | FlagDeleted)) != 0 : parentRecord != RootRecord)
            {
                if (idx >= 0) { MarkDeleted(idx); map[record] = 0; } // moved into one
                return;
            }
        }
        if (idx >= 0)
        {
            if (!utf8.SequenceEqual(NameBytes(idx)))
            {
                _wastedBytes += _nameLen[idx];
                (_nameStart[idx], _nameLen[idx]) = (AppendName(utf8, idx), (ushort)length);
            }
        }
        else
        {
            if (_count == _parent.Length) ResizeEntries(_count + _count / 2 + 1024);
            idx = _count++;
            (_nameStart[idx], _nameLen[idx]) = (AppendName(utf8, idx), (ushort)length);
            _volume[idx] = (byte)volume;
            _sequence[idx] = sequence;
            map[record] = idx + 1;
        }
        _flags[idx] = flags;
        if (_bulk[volume]) _parent[idx] = -2 - parentRecord; // resolved in EndVolume (parents may come later)
        else
        {
            _parent[idx] = parentRecord < map.Length ? map[parentRecord] - 1 : -1; // -1: the volume root
            if (_skipJunk && IsPruneRoot(idx)) _flags[idx] |= FlagPruned;
        }
    }

    private void DeleteCore(int volume, long frn)
    {
        var map = _records[volume];
        int record = RecordOf(frn);
        if (map == null || record >= map.Length) return;
        int idx = map[record] - 1;
        if (idx < 0 || _sequence[idx] != SequenceOf(frn)) return;
        map[record] = 0;
        MarkDeleted(idx);
    }

    private void MarkDeleted(int idx)
    {
        if ((_flags[idx] & FlagDeleted) != 0) return;
        _flags[idx] |= FlagDeleted;
        _wastedBytes += _nameLen[idx];
        _deleted++;
    }

    private void ClearVolumeCore(int volume)
    {
        if (_records[volume] == null) return;
        for (int i = 0; i < _count; i++)
            if (_volume[i] == volume) MarkDeleted(i);
        _records[volume] = null;
        CompactIfWorthIt();
    }

    /// <summary>Stores a name at the end of the buffer for entry <paramref name="entry"/>.</summary>
    private int AppendName(ReadOnlySpan<byte> utf8, int entry)
    {
        // Never compact here: the caller holds entry indexes. Writer.Dispose compacts when it is worth it.
        if (_namesUsed + utf8.Length > _names.Length)
            Array.Resize(ref _names, (int)Math.Min(Math.Max(_names.Length + (long)_names.Length / 2, _namesUsed + utf8.Length), Array.MaxLength));
        int start = _namesUsed;
        utf8.CopyTo(_names.AsSpan(start));
        _namesUsed += utf8.Length;
        if (_runs == _runStart.Length) ResizeRuns(_runs + _runs / 2 + 1024);
        _runStart[_runs] = start;
        _runEntry[_runs] = entry;
        _runs++;
        return start;
    }

    private void ResizeEntries(int size)
    {
        Array.Resize(ref _parent, size);
        Array.Resize(ref _nameStart, size);
        Array.Resize(ref _nameLen, size);
        Array.Resize(ref _sequence, size);
        Array.Resize(ref _flags, size);
        Array.Resize(ref _volume, size);
    }

    private void ResizeRuns(int size)
    {
        Array.Resize(ref _runStart, size);
        Array.Resize(ref _runEntry, size);
    }

    private void CompactIfWorthIt()
    {
        if (_bulk.Any(b => b)) return; // entry numbers must stay stable while a volume's parents are pending
        if ((_deleted > 50_000 && _deleted > _count / 4) || (_wastedBytes > 2_000_000 && _wastedBytes > _namesUsed / 3)) Compact();
    }

    /// <summary>Drops deleted entries and unused name space, renumbering entries.</summary>
    private void Compact()
    {
        if (_bulk.Any(b => b)) return;
        var remap = new int[_count];
        int live = 0;
        for (int i = 0; i < _count; i++) remap[i] = (_flags[i] & FlagDeleted) != 0 ? -1 : live++;
        var names = new byte[Math.Max(16384, _namesUsed - _wastedBytes + 16384)];
        var runStart = new int[live + 1024];
        var runEntry = new int[live + 1024];
        int used = 0;
        for (int i = 0; i < _count; i++)
        {
            int to = remap[i];
            if (to < 0) continue;
            _names.AsSpan(_nameStart[i], _nameLen[i]).CopyTo(names.AsSpan(used));
            runStart[to] = used;
            runEntry[to] = to;
            _nameStart[to] = used;
            used += _nameLen[i];
            _nameLen[to] = _nameLen[i];
            _sequence[to] = _sequence[i];
            _flags[to] = _flags[i];
            _volume[to] = _volume[i];
            _parent[to] = _parent[i] >= 0 ? remap[_parent[i]] : -1;
        }
        foreach (var map in _records)
        {
            if (map == null) continue;
            for (int r = 0; r < map.Length; r++)
                if (map[r] > 0) map[r] = map[r] - 1 < remap.Length ? remap[map[r] - 1] + 1 : 0;
        }
        (_names, _namesUsed, _wastedBytes) = (names, used, 0);
        (_runStart, _runEntry, _runs) = (runStart, runEntry, live);
        _count = live;
        _deleted = 0;
        ResolveExcluded();
    }

    // ---- Saving and loading ----------------------------------------------------------------------------

    private const int SnapshotMagic = 0x58494443; // "CDIX"
    private const int SnapshotVersion = 1;

    /// <summary>Volumes that currently have data (drive letter indexes).</summary>
    public IReadOnlyList<int> LoadedVolumes
    {
        get { _lock.EnterReadLock(); try { return Enumerable.Range(0, 26).Where(v => _records[v] != null).ToList(); } finally { _lock.ExitReadLock(); } }
    }

    /// <summary>
    /// Writes the whole index to <paramref name="path"/> (via a temporary file). <paramref name="underLock"/> runs
    /// while the index cannot change, so whatever it captures (the journal checkpoints) matches the file exactly.
    /// </summary>
    public void Save(string path, Action underLock)
    {
        string tmp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _lock.EnterReadLock();
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(SnapshotMagic);
                w.Write(SnapshotVersion);
                w.Write(_count);
                w.Write(_namesUsed);
                w.Write(_runs);
                fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_parent.AsSpan(0, _count)));
                fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_nameStart.AsSpan(0, _count)));
                fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_nameLen.AsSpan(0, _count)));
                fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_sequence.AsSpan(0, _count)));
                fs.Write(_flags.AsSpan(0, _count));
                fs.Write(_volume.AsSpan(0, _count));
                fs.Write(_names.AsSpan(0, _namesUsed));
                fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_runStart.AsSpan(0, _runs)));
                fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_runEntry.AsSpan(0, _runs)));
                for (int v = 0; v < 26; v++)
                {
                    var map = _records[v];
                    w.Write(map?.Length ?? -1);
                    if (map != null) fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(map.AsSpan()));
                }
            }
            underLock();
        }
        finally { _lock.ExitReadLock(); }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Replaces the index with a saved one; false (and the index unchanged) if the file is missing or unusable.</summary>
    public bool TryLoad(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var r = new BinaryReader(fs);
            if (r.ReadInt32() != SnapshotMagic || r.ReadInt32() != SnapshotVersion) return false;
            int count = r.ReadInt32(), namesUsed = r.ReadInt32(), runs = r.ReadInt32();
            if (count < 0 || namesUsed < 0 || runs < 0 || count > 200_000_000) return false;
            int capacity = count + count / 16 + 1024;
            var parent = new int[capacity];
            var nameStart = new int[capacity];
            var nameLen = new ushort[capacity];
            var sequence = new ushort[capacity];
            var flags = new byte[capacity];
            var volume = new byte[capacity];
            var names = new byte[namesUsed + namesUsed / 16 + 16384];
            var runStart = new int[runs + runs / 16 + 1024];
            var runEntry = new int[runStart.Length];
            fs.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(parent.AsSpan(0, count)));
            fs.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(nameStart.AsSpan(0, count)));
            fs.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(nameLen.AsSpan(0, count)));
            fs.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(sequence.AsSpan(0, count)));
            fs.ReadExactly(flags.AsSpan(0, count));
            fs.ReadExactly(volume.AsSpan(0, count));
            fs.ReadExactly(names.AsSpan(0, namesUsed));
            fs.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(runStart.AsSpan(0, runs)));
            fs.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(runEntry.AsSpan(0, runs)));
            var records = new int[26][];
            for (int v = 0; v < 26; v++)
            {
                int len = r.ReadInt32();
                if (len < 0) continue;
                if (len > 200_000_000) return false;
                records[v] = new int[len];
                fs.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(records[v].AsSpan()));
            }
            // Cheap sanity checks, so a damaged file cannot make searches index out of range.
            for (int i = 0; i < count; i++)
                if (parent[i] >= count || parent[i] < -1 || (uint)nameStart[i] + nameLen[i] > (uint)namesUsed || volume[i] >= 26) return false;
            for (int i = 0; i < runs; i++)
                if ((uint)runEntry[i] >= (uint)count || (uint)runStart[i] > (uint)namesUsed) return false;
            foreach (var map in records)
                if (map != null && map.Any(e => e < 0 || e > count)) return false;

            _lock.EnterWriteLock();
            try
            {
                (_parent, _nameStart, _nameLen, _sequence, _flags, _volume) = (parent, nameStart, nameLen, sequence, flags, volume);
                (_count, _names, _namesUsed, _runStart, _runEntry, _runs) = (count, names, namesUsed, runStart, runEntry, runs);
                for (int v = 0; v < 26; v++) _records[v] = records[v];
                Array.Clear(_bulk);
                _deleted = 0;
                _wastedBytes = 0;
                for (int i = 0; i < count; i++)
                    if ((flags[i] & FlagDeleted) != 0) { _deleted++; _wastedBytes += nameLen[i]; }
                if (Prune() > 0) { Compact(); TrimExcess(); } // saved before the setting was on
                ResolveExcluded();
            }
            finally { _lock.ExitWriteLock(); }
            return true;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or OutOfMemoryException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Could not load the saved file index");
            return false;
        }
    }

    // ---- Skipped folders ----------------------------------------------------------------------------

    /// <summary>
    /// Leaves out the contents of node_modules folders and of Windows' component store (C:\Windows\WinSxS): a quarter
    /// to a third of all files on a developer's machine, and never what anyone searches for. Turning it on prunes what
    /// is loaded; turning it off returns true: the caller must re-read the drives to get those files back.
    /// </summary>
    public bool SetSkipJunk(bool on)
    {
        _lock.EnterWriteLock();
        try
        {
            bool wasOn = _skipJunk;
            _skipJunk = on;
            if (on && !wasOn && Prune() > 0 && !_bulk.Any(b => b)) { Compact(); TrimExcess(); }
            return wasOn && !on;
        }
        finally { _lock.ExitWriteLock(); }
    }

    private static readonly byte[] NodeModules = "node_modules"u8.ToArray(), WinSxS = "winsxs"u8.ToArray(), Windows = "windows"u8.ToArray();

    private bool NameIs(int i, byte[] lowered) => _nameLen[i] == lowered.Length && EqualsFolded(NameBytes(i), lowered);

    /// <summary>A folder whose contents are skipped: any node_modules, and WinSxS directly in a drive's Windows folder.</summary>
    private bool IsPruneRoot(int i)
    {
        if ((_flags[i] & (FlagDirectory | FlagDeleted)) != FlagDirectory) return false;
        if (NameIs(i, NodeModules)) return true;
        int p = _parent[i];
        return NameIs(i, WinSxS) && p >= 0 && _parent[p] == -1 && NameIs(p, Windows);
    }

    /// <summary>Marks the skipped folders and drops everything below them (volumes still loading wait). Under the write lock.</summary>
    private int Prune()
    {
        if (!_skipJunk) return 0;
        for (int i = 0; i < _count; i++)
            if (!_bulk[_volume[i]] && IsPruneRoot(i)) _flags[i] |= FlagPruned;
        int dropped = 0;
        for (int i = 0; i < _count; i++)
        {
            if ((_flags[i] & FlagDeleted) != 0 || _bulk[_volume[i]]) continue;
            for (int a = _parent[i], depth = 0; a >= 0 && depth < 256; a = _parent[a], depth++)
                if ((_flags[a] & FlagPruned) != 0) { MarkDeleted(i); dropped++; break; }
        }
        return dropped;
    }

    // ---- Exclusions ---------------------------------------------------------------------------------

    /// <summary>Folders (full paths) whose contents never appear in results.</summary>
    public void SetExcludedFolders(IEnumerable<string> paths)
    {
        _lock.EnterWriteLock();
        try
        {
            _excludedPaths = paths.Select(p => p.Trim().TrimEnd('\\')).Where(p => p.Length >= 2).ToArray();
            ResolveExcluded();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Finds the entries of the excluded folders (by last name component, then full path). Under the write lock.</summary>
    private void ResolveExcluded()
    {
        var set = new HashSet<int>();
        foreach (string path in _excludedPaths)
        {
            string lastName = Path.GetFileName(path);
            byte[] last = Encoding.UTF8.GetBytes(lastName);
            if (last.Length == 0) continue;
            for (int i = 0; i < _count; i++)
            {
                if ((_flags[i] & (FlagDeleted | FlagDirectory)) != FlagDirectory || _nameLen[i] != last.Length) continue;
                if (!string.Equals(NameOf(i), lastName, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(PathOf(i), path, StringComparison.OrdinalIgnoreCase)) set.Add(i);
            }
        }
        _excluded = set;
    }

    private bool IsExcluded(int i)
    {
        var set = _excluded;
        if (set.Count == 0) return false;
        for (int depth = 0; i >= 0 && depth < 256; depth++, i = _parent[i])
            if (set.Contains(i)) return true;
        return false;
    }

    // ---- Reading -----------------------------------------------------------------------------------

    private ReadOnlySpan<byte> NameBytes(int i) => new(_names, _nameStart[i], _nameLen[i]);

    private string NameOf(int i) => Encoding.UTF8.GetString(_names, _nameStart[i], _nameLen[i]);

    private string PathOf(int i)
    {
        Span<int> chain = stackalloc int[256];
        int n = 0;
        for (int at = i; at >= 0 && n < chain.Length; at = _parent[at]) chain[n++] = at;
        var sb = new StringBuilder(128);
        sb.Append((char)('A' + _volume[i])).Append(":\\");
        Span<char> part = stackalloc char[MaxNameBytes];
        for (int k = n - 1; k >= 0; k--)
        {
            int len = Encoding.UTF8.GetChars(NameBytes(chain[k]), part);
            sb.Append(part[..len]);
            if (k > 0) sb.Append('\\');
        }
        return sb.ToString();
    }

    private const int Shortlist = 400;

    /// <summary>Per-thread search state: the best few hundred hits and how many matched in all.</summary>
    private sealed class Partial
    {
        public readonly List<(int Score, int Index)> Best = new();
        public int Total;
        public int Floor = int.MinValue; // once the list is full, hits must beat this to matter
    }

    public FileSearchResult Search(FileQuery query, int limit, bool includeHidden, CancellationToken token = default)
    {
        if (query.IsEmpty || limit <= 0) return FileSearchResult.None;
        _lock.EnterReadLock();
        try
        {
            var partials = new List<Partial>();
            var options = new ParallelOptions { CancellationToken = token };
            var scans = ScanPatterns(query);
            if (scans != null)
            {
                // Scan the name buffer for the most selective literal; check only the entries where it occurs.
                int used = _namesUsed;
                int segments = Math.Max(1, Math.Min(Environment.ProcessorCount * 2, used / 65536 + 1));
                int segSize = used / segments + 1;
                foreach (var pattern in scans)
                {
                    Parallel.For(0, segments, options, () => new Partial(), (s, _, local) =>
                    {
                        ScanSegment(pattern, s * segSize, Math.Min(used, (s + 1) * segSize), query, includeHidden, local);
                        return local;
                    }, local => { lock (partials) partials.Add(local); });
                }
            }
            else
            {
                // Nothing literal to look for (folder:, a wildcard like a*b, letters with case outside ASCII): check every entry.
                int count = _count;
                const int Chunk = 1 << 16;
                Parallel.For(0, (count + Chunk - 1) / Chunk, options, () => new Partial(), (chunk, _, local) =>
                {
                    int end = Math.Min(count, (chunk + 1) * Chunk);
                    for (int i = chunk * Chunk; i < end; i++) Consider(i, query, includeHidden, local);
                    return local;
                }, local => { lock (partials) partials.Add(local); });
            }

            // Re-rank the best few hundred with their full paths (depth, system folders, the user's own folders).
            var ranked = partials.SelectMany(p => p.Best)
                .OrderByDescending(s => s.Score).Take(Shortlist)
                .Select(s => (Path: PathOf(s.Index), s.Score, s.Index))
                .Select(s => (s.Path, Score: s.Score + PathScore(s.Path), s.Index))
                .OrderByDescending(s => s.Score).ThenBy(s => s.Path.Length)
                .Take(limit)
                .Select(s => new FileHit(s.Path, NameOf(s.Index), (_flags[s.Index] & FlagDirectory) != 0))
                .ToList();
            return new FileSearchResult(ranked, partials.Sum(p => p.Total));
        }
        catch (OperationCanceledException) { return FileSearchResult.None; }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// UTF-8 literals to scan for, one scan each (results are unioned): the longest name term; otherwise ".ext" for
    /// each extension filter; otherwise the longest literal run of a wildcard. ASCII letters match either case;
    /// a term with letters that have case outside ASCII (É, Ж) cannot be scanned bytewise. Null: check every entry.
    /// </summary>
    private static List<byte[]>? ScanPatterns(FileQuery q)
    {
        static byte[]? Pattern(string s) =>
            s.Any(c => c >= 128 && char.ToUpperInvariant(c) != char.ToLowerInvariant(c)) ? null : Encoding.UTF8.GetBytes(s.ToLowerInvariant());
        if (q.NameTerms.Count > 0) return Pattern(q.NameTerms[0]) is { } p ? [p] : null;
        if (q.Extensions.Count > 0)
        {
            var list = q.Extensions.Distinct(StringComparer.OrdinalIgnoreCase).Select(e => Pattern("." + e)).ToList();
            return list.All(p => p != null) ? list.Select(p => p!).ToList() : null;
        }
        if (q.WildcardLiteral is { Length: >= 2 } literal) return Pattern(literal) is { } w ? [w] : null;
        return null;
    }

    /// <summary>
    /// Finds the (ASCII case-insensitive) pattern in [from, to) of the buffer and considers each owning entry once.
    /// The vectorised search looks for the pattern's least common byte (the "y" of "pinyin", not the "p"), so few
    /// candidates need checking.
    /// </summary>
    private void ScanSegment(byte[] pattern, int from, int to, FileQuery query, bool includeHidden, Partial local)
    {
        if (from >= to) return;
        int end = Math.Min(_namesUsed, to + pattern.Length - 1); // a match starting in this segment may run past it
        var hay = new ReadOnlySpan<byte>(_names, 0, end);
        int k = AnchorOf(pattern);
        byte lo = pattern[k];
        byte up = lo is >= (byte)'a' and <= (byte)'z' ? (byte)(lo - 32) : lo;
        int run = -1, lastEntry = -1;
        int q = from + k;          // anchor position; the match starts at q - k
        int qEnd = Math.Min(end, to + k);
        while (q < qEnd)
        {
            var window = hay[q..qEnd];
            int at = lo == up ? window.IndexOf(lo) : window.IndexOfAny(lo, up);
            if (at < 0) break;
            int p = q + at - k;
            q += at + 1;
            if (p + pattern.Length > end || !EqualsFolded(hay.Slice(p, pattern.Length), pattern)) continue;
            // The stored name containing p: the last run that starts at or before p (runs are in buffer order).
            if (!(run >= 0 && _runStart[run] <= p && (run + 1 == _runs || _runStart[run + 1] > p))) run = FindRun(p);
            if (run < 0) continue;
            int entry = _runEntry[run];
            int start = _runStart[run];
            if (entry == lastEntry || _nameStart[entry] != start || p + pattern.Length > start + _nameLen[entry]) continue; // stale, or spans two names
            lastEntry = entry;
            Consider(entry, query, includeHidden, local);
        }
    }

    private const string LetterFrequency = "zqjxkvbywgpfmucdlhrsnioate"; // rarest first, roughly as in file names

    /// <summary>Index of the byte least likely to occur in names (rare letters, UTF-8 continuation bytes).</summary>
    private static int AnchorOf(byte[] pattern)
    {
        int best = 0, bestCost = int.MaxValue;
        for (int i = 0; i < pattern.Length; i++)
        {
            byte b = pattern[i];
            int cost = b switch
            {
                >= (byte)'a' and <= (byte)'z' => 10 + LetterFrequency.IndexOf((char)b),
                >= (byte)'0' and <= (byte)'9' => 30,
                (byte)'.' or (byte)' ' or (byte)'_' or (byte)'-' => 40,
                >= 0x80 and <= 0xBF => 5,   // UTF-8 continuation byte: varies a lot between characters
                >= 0xC0 => 36,              // UTF-8 lead byte: shared by whole blocks (all common CJK start E4–E9)
                _ => 8,
            };
            if (cost <= bestCost) (best, bestCost) = (i, cost);
        }
        return best;
    }

    /// <summary>Bytes equal to a lower-cased pattern, ASCII letters in either case (other bytes exactly).</summary>
    private static bool EqualsFolded(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> lowered)
    {
        for (int i = 0; i < lowered.Length; i++)
        {
            byte h = hay[i], l = lowered[i];
            if (h != l && !(l is >= (byte)'a' and <= (byte)'z' && h == l - 32)) return false;
        }
        return true;
    }

    private int FindRun(int position)
    {
        int lo = 0, hi = _runs - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_runStart[mid] <= position) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    /// <summary>Applies every filter to entry <paramref name="i"/> and keeps it if it matches.</summary>
    private void Consider(int i, FileQuery query, bool includeHidden, Partial local)
    {
        byte f = _flags[i];
        if ((f & FlagDeleted) != 0) return;
        if (!includeHidden && (f & (FlagHidden | FlagSystem)) != 0) return;
        if (query.FoldersOnly && (f & FlagDirectory) == 0) return;
        if (query.FilesOnly && (f & FlagDirectory) != 0) return;
        Span<char> name = stackalloc char[MaxNameBytes];
        name = name[..Encoding.UTF8.GetChars(NameBytes(i), name)];
        int score = NameScore(name, query);
        if (score <= 0) return;
        if (IsExcluded(i)) return;
        if (query.PathTerms.Count > 0 && !PathMatches(i, query)) return;
        local.Total++;
        if ((f & FlagDirectory) != 0) score += 5;
        if (score <= local.Floor) return;
        var list = local.Best;
        list.Add((score, i));
        if (list.Count >= Shortlist * 2)
        {
            list.Sort((a, b) => b.Score.CompareTo(a.Score));
            list.RemoveRange(Shortlist, list.Count - Shortlist);
            local.Floor = list[^1].Score;
        }
    }

    private static int NameScore(ReadOnlySpan<char> name, FileQuery q)
    {
        if (!q.ExtensionMatches(name)) return 0;
        if (q.Wildcard != null && !q.Wildcard.IsMatch(name)) return 0;
        int score = 100;
        for (int t = 0; t < q.NameTerms.Count; t++)
        {
            string term = q.NameTerms[t];
            int at = name.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return 0;
            if (t > 0) continue; // only the main term shapes the score
            int dot = name.LastIndexOf('.');
            var stem = dot > 0 ? name[..dot] : name;
            if (name.Length == term.Length || (stem.Length == term.Length && at == 0)) score += 300;
            else if (at == 0) score += 200;
            else if (!char.IsLetterOrDigit(name[at - 1])) score += 100;
        }
        return score - Math.Min(name.Length, 120) / 4;
    }

    private bool PathMatches(int i, FileQuery q)
    {
        string path = PathOf(i);
        foreach (string term in q.PathTerms)
            if (path.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }

    private static readonly (string Part, int Score)[] PathScores =
    [
        (@"\$recycle.bin", -150), (@"\windows\winsxs\", -120), (@"\windows\", -80), (@"\appdata\", -60), (@"\node_modules\", -60),
        (@"\.git\", -60), (@"\programdata\", -40), (@"\program files", -20), (@"\obj\", -20), (@"\bin\", -10),
        (@"\desktop\", 40), (@"\documents\", 30), (@"\downloads\", 30), (@"\pictures\", 20), (@"\videos\", 20), (@"\music\", 20),
    ];

    private static int PathScore(string path)
    {
        int score = 0;
        foreach (var (part, s) in PathScores)
            if (path.Contains(part, StringComparison.OrdinalIgnoreCase)) score += s;
        int depth = path.Count(c => c == '\\');
        return score - depth * 3;
    }
}
