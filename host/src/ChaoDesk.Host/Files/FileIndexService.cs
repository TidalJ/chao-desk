using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using ChaoDesk.Host.Localization;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Files;

public enum FileIndexState { Off, NotSetUp, NeedsRepair, Starting, Indexing, Ready, Failed }

/// <summary>
/// App side of file search: owns the <see cref="FileIndex"/>, the pipe the elevated helper writes into, and the
/// helper's lifecycle (start it through the scheduled task, restart it if it dies). The index is saved to disk on
/// exit and every half hour and loaded before the helper starts, so search works the moment the app is up and the
/// helper only replays what changed meanwhile. FAT and exFAT drives, which have no change journal, are
/// scanned by <see cref="FatScanner"/>. Status changes are raised on a background thread.
/// </summary>
public sealed class FileIndexService : IDisposable
{
    private static string SnapshotPath => Path.Combine(IndexCheckpoints.Folder, "files.bin");

    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly HashSet<int> _loading = new();
    private readonly List<string> _volumeErrors = new();
    private readonly Dictionary<char, IndexCheckpoints.Entry> _checkpoints = new(); // changed under the index's write lock
    private readonly Timer _saveTimer;
    private FatScanner? _fat;
    private CancellationTokenSource? _cts;
    private int _restarts;
    private bool _disposed;
    private bool _snapshotTried;
    private bool _fromSnapshot;
    private long _changes;      // applied changes since the last save
    private long _savedChanges;

    public FileIndex Index { get; } = new();
    public FileIndexState State { get; private set; } = FileIndexState.Off;
    public bool IncludeHidden { get; set; }
    public event Action? StatusChanged;

    public FileIndexService()
    {
        _saveTimer = new Timer(_ => SaveIfChanged(), null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

    /// <summary>One line for the launcher footer and the settings page.</summary>
    public string StatusText
    {
        get
        {
            string errors;
            lock (_volumeErrors) errors = _volumeErrors.Count > 0 ? $" ({string.Join("; ", _volumeErrors)})" : "";
            return State switch
            {
                FileIndexState.Off => Loc.T("File search is off."),
                FileIndexState.NotSetUp => Loc.T("File search is not set up yet."),
                FileIndexState.NeedsRepair => Loc.T("File search must be set up again: Chao Desk was moved or reinstalled."),
                FileIndexState.Starting when _fromSnapshot => Loc.F("{0:N0} files and folders (saved index; catching up)", Index.Count),
                FileIndexState.Starting => Loc.T("Starting file search…"),
                FileIndexState.Indexing => Loc.F("Indexing files… {0:N0} so far{1}", Index.Count, errors),
                FileIndexState.Ready => Loc.F("{0:N0} files and folders indexed{1}", Index.Count, errors),
                _ => Loc.F("File search stopped{0}.", errors),
            };
        }
    }

    public bool CanSetUp => State is FileIndexState.NotSetUp or FileIndexState.NeedsRepair or FileIndexState.Failed && Index.Count == 0;

    /// <summary>Searching makes sense whenever there is data, even while the helper (re)connects.</summary>
    public bool CanSearch => State is FileIndexState.Indexing or FileIndexState.Ready || (State is FileIndexState.Starting or FileIndexState.Failed && Index.Count > 0);

    // ---- Lifecycle -----------------------------------------------------------------------------------

    /// <summary>Starts the helper if the task exists (no prompt); otherwise reports that setup is needed.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _cts != null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _restarts = 0;
            Task.Run(() => RunAsync(token), token);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
        }
        _fat?.Dispose();
        _fat = null;
        SetState(FileIndexState.Off);
    }

    /// <summary>Creates the scheduled task (one UAC prompt) and starts indexing. False if the user declined.</summary>
    public async Task<bool> SetUpAsync()
    {
        bool ok = await IndexerTask.RunElevatedAsync(IndexerTask.RegisterArguments());
        Log.Information("File index setup {Result}", ok ? "succeeded" : "was declined or failed");
        if (!ok) return false;
        Stop();
        Start();
        return true;
    }

    /// <summary>Forgets the saved index and reads every drive again (after skipped folders are let back in).</summary>
    public void Rebuild()
    {
        Stop();
        lock (_saveGate)
        {
            try { File.Delete(SnapshotPath); } catch (IOException) { }
            IndexCheckpoints.Delete(IndexCheckpoints.DefaultPath);
            using (Index.BeginWrite()) _checkpoints.Clear();
            _fromSnapshot = false;
        }
        Log.Information("Re-reading every drive for file search");
        Start();
    }

    /// <summary>Removes the scheduled task (one UAC prompt) and the saved index.</summary>
    public async Task<bool> RemoveAsync()
    {
        Stop();
        bool ok = await IndexerTask.RunElevatedAsync(IndexerTask.UnregisterArguments);
        try { File.Delete(SnapshotPath); } catch (IOException) { }
        IndexCheckpoints.Delete(IndexCheckpoints.DefaultPath);
        SetState(FileIndexState.NotSetUp);
        return ok;
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            switch (IndexerTask.Query())
            {
                case IndexerTask.TaskState.Missing: SetState(FileIndexState.NotSetUp); return;
                case IndexerTask.TaskState.WrongPath: SetState(FileIndexState.NeedsRepair); return;
            }
            if (!_snapshotTried) LoadSnapshot();
            SetState(FileIndexState.Starting);
            _fat ??= new FatScanner(Index, () => { Interlocked.Increment(ref _changes); StatusChanged?.Invoke(); });
            try
            {
                await ServeOnceAsync(token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
            {
                Log.Warning("File index connection ended: {Message}", ex.Message);
            }
            if (token.IsCancellationRequested) return;
            if (++_restarts > 5)
            {
                SetState(FileIndexState.Failed);
                return;
            }
            SetState(FileIndexState.Failed);
            await Task.Delay(TimeSpan.FromSeconds(5 * _restarts), token).ContinueWith(_ => { });
        }
    }

    /// <summary>
    /// Loads the saved index and its checkpoints before the helper starts. Without a usable index the checkpoints
    /// are deleted, so the helper reads every drive in full instead of resuming onto nothing.
    /// </summary>
    private void LoadSnapshot()
    {
        _snapshotTried = true;
        var started = Stopwatch.StartNew();
        if (Index.TryLoad(SnapshotPath))
        {
            var saved = IndexCheckpoints.Read(IndexCheckpoints.DefaultPath);
            using (Index.BeginWrite())
                foreach (var (letter, entry) in saved) _checkpoints[letter] = entry;
            // Drives that are not there now (an unplugged USB disk) must not linger in results.
            foreach (int v in Index.LoadedVolumes)
            {
                char letter = (char)('A' + v);
                if (Directory.Exists($@"{letter}:\")) continue;
                using (var w = Index.BeginWrite()) { w.ClearVolume(v); _checkpoints.Remove(letter); }
            }
            _fromSnapshot = true;
            Log.Information("Loaded the saved file index: {Count:N0} entries in {Ms} ms", Index.Count, started.ElapsedMilliseconds);
        }
        else IndexCheckpoints.Delete(IndexCheckpoints.DefaultPath);
    }

    /// <summary>Saves the index if anything changed since the last save (timer, exit).</summary>
    public void SaveIfChanged()
    {
        lock (_saveGate) SaveCore();
    }

    private void SaveCore()
    {
        long changes = Interlocked.Read(ref _changes);
        if (changes == _savedChanges || State is not (FileIndexState.Ready or FileIndexState.Starting) || Index.Count == 0) return;
        try
        {
            var started = Stopwatch.StartNew();
            Dictionary<char, IndexCheckpoints.Entry> checkpoints = new();
            Index.Save(SnapshotPath, () => { foreach (var kv in _checkpoints) checkpoints[kv.Key] = kv.Value; });
            IndexCheckpoints.Write(IndexCheckpoints.DefaultPath, checkpoints);
            _savedChanges = changes;
            Log.Information("Saved the file index in {Ms} ms", started.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not save the file index");
        }
    }

    /// <summary>One helper session: listen, start the task, verify the client, then apply what it sends until it ends.</summary>
    private async Task ServeOnceAsync(CancellationToken token)
    {
        string name = IndexProtocol.PipeName(Process.GetCurrentProcess().SessionId);
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 1 << 20, 0);
        var connect = pipe.WaitForConnectionAsync(token);
        if (!IndexerTask.Start()) throw new IOException("the file index task could not be started");
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await connect.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) ||
            !string.Equals(GetProcessPath(pid), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"unexpected file index client (process {pid})");

        Log.Information("File index helper connected (process {Pid})", pid);
        lock (_volumeErrors) _volumeErrors.Clear();
        using var reader = new BinaryReader(new BufferedStream(pipe, 1 << 20), Encoding.UTF8);
        await Task.Run(() => ReadLoop(reader, token), token);
    }

    /// <summary>A message waiting to be applied (names live in the batch's shared buffer).</summary>
    private struct Op
    {
        public byte Type;
        public byte Volume;
        public long Frn;
        public long Parent; // or the estimate (VolumeBegin), or the USN (Checkpoint)
        public ulong Journal;
        public uint Attributes;
        public int NameStart;
        public int NameLength;
    }

    /// <summary>
    /// Messages are parsed without holding the index lock and applied in batches: at every ping (the helper sends
    /// one with each flush, four times a second) or every 20,000 messages. Reading never blocks under the lock.
    /// </summary>
    private void ReadLoop(BinaryReader r, CancellationToken token)
    {
        if (r.ReadByte() != IndexProtocol.Hello || r.ReadInt32() != IndexProtocol.Version) throw new InvalidDataException("file index protocol mismatch");
        var ops = new List<Op>(20_000);
        var chars = new char[1 << 20];
        int used = 0;
        var lastStatus = Stopwatch.StartNew();
        while (!token.IsCancellationRequested)
        {
            byte type = r.ReadByte();
            switch (type)
            {
                case IndexProtocol.Entry:
                {
                    var op = new Op { Type = type, Volume = r.ReadByte(), Frn = r.ReadInt64(), Parent = r.ReadInt64(), Attributes = r.ReadUInt32(), NameLength = r.ReadUInt16() };
                    if (used + op.NameLength > chars.Length) Array.Resize(ref chars, chars.Length * 2);
                    op.NameStart = used;
                    r.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chars.AsSpan(used, op.NameLength)));
                    used += op.NameLength;
                    ops.Add(op);
                    break;
                }
                case IndexProtocol.Delete:
                    ops.Add(new Op { Type = type, Volume = r.ReadByte(), Frn = r.ReadInt64() });
                    break;
                case IndexProtocol.VolumeBegin:
                    ops.Add(new Op { Type = type, Volume = r.ReadByte(), Parent = r.ReadInt64() });
                    break;
                case IndexProtocol.VolumeReady or IndexProtocol.VolumeResume or IndexProtocol.VolumeGone:
                    ops.Add(new Op { Type = type, Volume = r.ReadByte() });
                    break;
                case IndexProtocol.Checkpoint:
                    ops.Add(new Op { Type = type, Volume = r.ReadByte(), Journal = r.ReadUInt64(), Parent = r.ReadInt64() });
                    break;
                case IndexProtocol.Ping:
                    break;
                case IndexProtocol.Error:
                    string message = r.ReadString();
                    lock (_volumeErrors) _volumeErrors.Add(message);
                    Log.Warning("File index helper: {Message}", message);
                    StatusChanged?.Invoke();
                    break;
                default:
                    throw new InvalidDataException($"unknown file index message {type}");
            }
            if (type == IndexProtocol.Ping ? ops.Count > 0 : ops.Count >= 20_000)
            {
                Apply(ops, chars);
                ops.Clear();
                used = 0;
                if (lastStatus.ElapsedMilliseconds > 500 || State == FileIndexState.Ready)
                {
                    lastStatus.Restart();
                    StatusChanged?.Invoke();
                }
            }
        }
    }

    private void Apply(List<Op> ops, char[] chars)
    {
        bool begun = false, ready = false, resumedWithoutData = false;
        int changes = 0;
        using (var w = Index.BeginWrite())
        {
            foreach (var op in ops)
            {
                if (op.Volume >= 26) continue;
                char letter = (char)('A' + op.Volume);
                switch (op.Type)
                {
                    case IndexProtocol.Entry: w.Upsert(op.Volume, op.Frn, op.Parent, op.Attributes, chars.AsSpan(op.NameStart, op.NameLength)); changes++; break;
                    case IndexProtocol.Delete: w.Delete(op.Volume, op.Frn); changes++; break;
                    case IndexProtocol.Checkpoint: _checkpoints[letter] = new IndexCheckpoints.Entry(op.Journal, op.Parent); break;
                    case IndexProtocol.VolumeBegin:
                        w.BeginVolume(op.Volume, op.Parent);
                        _checkpoints.Remove(letter);
                        lock (_loading) _loading.Add(op.Volume);
                        begun = true;
                        changes++;
                        break;
                    case IndexProtocol.VolumeResume:
                        if (!_checkpoints.ContainsKey(letter)) resumedWithoutData = true;
                        break;
                    case IndexProtocol.VolumeReady:
                        w.EndVolume(op.Volume);
                        Log.Information("Volume {Drive} indexed", $"{letter}:");
                        lock (_loading) { _loading.Remove(op.Volume); ready = _loading.Count == 0; }
                        break;
                    case IndexProtocol.VolumeGone:
                        w.ClearVolume(op.Volume);
                        _checkpoints.Remove(letter);
                        Log.Information("Volume {Drive} removed", $"{letter}:");
                        changes++;
                        break;
                }
            }
        }
        Interlocked.Add(ref _changes, changes);
        if (resumedWithoutData)
        {
            // The helper resumed a volume we hold no data for (a stale checkpoints file): make it read everything.
            IndexCheckpoints.Delete(IndexCheckpoints.DefaultPath);
            throw new InvalidDataException("the helper resumed a volume without a saved index; restarting it");
        }
        if (begun && !ready) SetState(FileIndexState.Indexing);
        if (ready)
        {
            var gc = GC.GetGCMemoryInfo();
            Log.Information("File index ready: {Count:N0} entries, about {Mb:N0} MB (managed heap {Heap:N0} MB, committed {Committed:N0} MB)",
                Index.Count, Index.MemoryBytes >> 20, gc.HeapSizeBytes >> 20, gc.TotalCommittedBytes >> 20);
            SetState(FileIndexState.Ready);
            _saveTimer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30)); // keep a fresh full read safe soon
        }
    }

    private void SetState(FileIndexState state)
    {
        if (State == state && state != FileIndexState.Indexing) return;
        State = state;
        StatusChanged?.Invoke();
    }

    // ---- Search --------------------------------------------------------------------------------------

    public FileSearchResult Search(string text, int limit, CancellationToken token = default)
    {
        if (!CanSearch) return FileSearchResult.None;
        return Index.Search(FileQuery.Parse(text), limit, IncludeHidden, token);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _cts?.Cancel();
            _cts = null;
        }
        _saveTimer.Dispose();
        _fat?.Dispose();
        SaveIfChanged();
    }
}
