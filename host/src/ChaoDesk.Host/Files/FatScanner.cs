using System.IO;
using Serilog;

namespace ChaoDesk.Host.Files;

/// <summary>
/// Indexes drives without an NTFS change journal (FAT32 and exFAT: USB sticks, SD cards). No administrator rights
/// are needed: the drive is walked directory by directory, then watched with a FileSystemWatcher; changes trigger
/// a rescan 15 seconds later, and every drive is rescanned every 15 minutes. Drives are noticed within 15 seconds
/// of being plugged in and dropped when removed. Each file gets a synthetic reference number for the index.
/// </summary>
public sealed class FatScanner : IDisposable
{
    private readonly FileIndex _index;
    private readonly Action _changed;
    private readonly Timer _poll;
    private readonly Dictionary<char, Drive> _drives = new();
    private readonly object _gate = new();
    private bool _disposed;
    private int _polling;

    private sealed class Drive
    {
        public FileSystemWatcher? Watcher;
        public DateTime LastScan;
        public DateTime DirtySince = DateTime.MaxValue;
        public bool Scanning;
    }

    public FatScanner(FileIndex index, Action changed)
    {
        _index = index;
        _changed = changed;
        _poll = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15));
    }

    private void Poll()
    {
        if (_disposed || Interlocked.Exchange(ref _polling, 1) == 1) return; // a long scan outlasts the timer period
        try { PollCore(); }
        finally { Volatile.Write(ref _polling, 0); }
    }

    private void PollCore()
    {
        var present = new HashSet<char>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.Name.Length < 2 || !char.IsAsciiLetter(d.Name[0]) || d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                if (d.DriveFormat is not ("FAT" or "FAT32" or "exFAT")) continue;
            }
            catch (IOException) { continue; }
            char letter = char.ToUpperInvariant(d.Name[0]);
            present.Add(letter);
            Drive drive;
            lock (_gate)
            {
                if (!_drives.TryGetValue(letter, out drive!)) _drives[letter] = drive = new Drive();
                if (drive.Scanning) continue;
                bool due = drive.LastScan == default || DateTime.Now - drive.LastScan > TimeSpan.FromMinutes(15) || DateTime.Now - drive.DirtySince > TimeSpan.FromSeconds(15);
                if (!due) continue;
                drive.Scanning = true;
                drive.DirtySince = DateTime.MaxValue;
            }
            Scan(letter, drive);
        }
        lock (_gate)
        {
            foreach (char gone in _drives.Keys.Where(k => !present.Contains(k)).ToList())
            {
                _drives[gone].Watcher?.Dispose();
                _drives.Remove(gone);
                using (var w = _index.BeginWrite()) w.ClearVolume(gone - 'A');
                Log.Information("Drive {Drive}: removed from the file index", $"{gone}:");
                _changed();
            }
        }
    }

    private void Scan(char letter, Drive drive)
    {
        int volume = letter - 'A';
        string root = $@"{letter}:\";
        var started = Environment.TickCount64;
        long next = 16; // synthetic record numbers; 5 is the (never stored) root, as on NTFS
        int count = 0;
        try
        {
            var batch = new List<(long Id, long Parent, uint Attributes, string Name)>(10_000);
            using (var w = _index.BeginWrite()) w.BeginVolume(volume, 1024);
            try
            {
                var pending = new Stack<(string Path, long Id)>();
                pending.Push((root, 5));
                var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
                while (pending.Count > 0 && !_disposed)
                {
                    var (dir, dirId) = pending.Pop();
                    IEnumerable<FileSystemInfo> entries;
                    try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    foreach (var e in entries)
                    {
                        long id = next++;
                        batch.Add((id, dirId, (uint)e.Attributes, e.Name));
                        if ((e.Attributes & FileAttributes.Directory) != 0 && (e.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push((e.FullName, id));
                        if (batch.Count == batch.Capacity) Flush(volume, batch, ref count);
                    }
                }
                Flush(volume, batch, ref count);
            }
            finally
            {
                using var w = _index.BeginWrite();
                w.EndVolume(volume); // always: a volume left "loading" would block compaction
            }
            Log.Information("Drive {Drive} ({Count:N0} entries) scanned in {Ms} ms", $"{letter}:", count, Environment.TickCount64 - started);
            Watch(letter, drive, root);
            _changed();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Information(ex, "Could not scan {Drive}", root);
        }
        finally
        {
            lock (_gate)
            {
                drive.Scanning = false;
                drive.LastScan = DateTime.Now;
            }
        }
    }

    private void Flush(int volume, List<(long Id, long Parent, uint Attributes, string Name)> batch, ref int count)
    {
        if (batch.Count == 0) return;
        using (var w = _index.BeginWrite())
            foreach (var (id, parent, attrs, name) in batch) w.Upsert(volume, id, parent, attrs, name);
        count += batch.Count;
        batch.Clear();
    }

    private void Watch(char letter, Drive drive, string root)
    {
        if (drive.Watcher != null) return;
        try
        {
            var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, InternalBufferSize = 64 * 1024 };
            void Dirty(object? _, EventArgs __) { lock (_gate) if (drive.DirtySince == DateTime.MaxValue) drive.DirtySince = DateTime.Now; }
            watcher.Created += Dirty;
            watcher.Deleted += Dirty;
            watcher.Renamed += Dirty;
            watcher.Error += Dirty;
            watcher.EnableRaisingEvents = true;
            drive.Watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Information(ex, "Cannot watch {Drive}; it is rescanned every 15 minutes", root);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _poll.Dispose();
        lock (_gate)
        {
            foreach (var d in _drives.Values) d.Watcher?.Dispose();
            _drives.Clear();
        }
    }
}
