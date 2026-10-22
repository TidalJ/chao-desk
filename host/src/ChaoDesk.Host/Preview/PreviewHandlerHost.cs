using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Preview;

/// <summary>
/// Hosts a Windows preview handler — the same component Explorer's preview pane uses (Word, Excel, PowerPoint,
/// Outlook messages, fonts, …, whatever the installed apps register) — in a child window.
/// </summary>
/// <remarks>
/// Starting a handler can take seconds (Office starts its server), so every handler call runs on one background STA
/// thread with its own message loop, never on the UI thread. The UI thread owns only an empty container window; the
/// handler draws into an inner window that the background thread creates inside it, hidden until the preview is
/// ready (a hidden window takes no input, so the UI never waits for a busy handler). Nothing is sent to the
/// background thread synchronously: sizes are posted, and closing hides the container at once and lets the
/// background thread tear the handler down before the container is destroyed.
/// </remarks>
public sealed class PreviewHandlerHost : HwndHost
{
    private const string HandlerKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private const uint STGM_READ = 0;
    private const uint STGM_SHARE_DENY_NONE = 0x40;

    private readonly string _path;
    private readonly Guid _clsid;
    private IntPtr _child;          // the container (UI thread)
    private bool _started;          // UI thread
    private volatile bool _closed;  // set on the UI thread, read on the handler thread

    // Handler thread only.
    private IntPtr _inner;
    private IPreviewHandler? _handler;
    private IStream? _stream;
    private bool _counted; // an out-of-process handler of ours is showing (counted in _active)

    public event Action<Exception>? Failed;

    /// <summary>The one thread every preview handler (and the warm instances below) lives on.</summary>
    private static readonly Lazy<Dispatcher> HandlerThread = new(() =>
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Preview handlers" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    });

    // Office's previewers run in WINWORD/POWERPNT started as COM servers. Starting one takes seconds (PowerPoint
    // 2–3 s) and releasing the last instance stalls another 1.5 s while the server shuts down. One idle instance per
    // such handler, kept for a few minutes after the last preview, keeps the server running: the next file shows in
    // a few hundred milliseconds. Measured: PowerPoint 4.7 s cold → 0.4 s warm.
    private static readonly Dictionary<Guid, object> Warm = new();
    private static System.Windows.Threading.DispatcherTimer? _warmTimer;
    private static readonly TimeSpan WarmFor = TimeSpan.FromMinutes(3);

    // Word's preview server does not exit when its last preview is released (PowerPoint's does, 3–4 s later),
    // measured: WINWORD -Embedding stayed for hours after a preview. The servers our previews started are
    // remembered, and ended once nothing of ours uses them — unless they show a window (the user opened a document
    // in that process). Started-by-us is told by the server processes present just before and after creating it.
    private static readonly HashSet<int> StartedServers = new();
    private static int _active;
    private static System.Windows.Threading.DispatcherTimer? _cleanupTimer;

    public PreviewHandlerHost(string path, Guid clsid)
    {
        _path = path;
        _clsid = clsid;
    }

    /// <summary>The preview handler registered for a file's extension (directly, via its ProgID or its perceived type), or null.</summary>
    public static Guid? HandlerFor(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext.Length == 0) return null;
        var candidates = new List<string> { $@"{ext}\shellex\{HandlerKey}", $@"SystemFileAssociations\{ext}\shellex\{HandlerKey}" };
        using (var extKey = Registry.ClassesRoot.OpenSubKey(ext))
        {
            if (extKey?.GetValue(null) is string progId && progId.Length > 0) candidates.Add($@"{progId}\shellex\{HandlerKey}");
            if (extKey?.GetValue("PerceivedType") is string perceived && perceived.Length > 0) candidates.Add($@"SystemFileAssociations\{perceived}\shellex\{HandlerKey}");
        }
        foreach (string sub in candidates)
        {
            using var key = Registry.ClassesRoot.OpenSubKey(sub);
            if (key?.GetValue(null) is string s && Guid.TryParse(s, out var clsid)) return clsid;
        }
        return null;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _child = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 0, 0, 100, 100, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, _child); // the handler starts once the window has its real size (OnRenderSizeChanged)
    }

    /// <summary>On the handler thread: the inner window, the handler, and the preview (shown once it is drawn).</summary>
    private void Start(RECT r, Dispatcher ui)
    {
        if (_closed) return;
        object? instance = null;
        try
        {
            // No parent notifications: they would be sent (and wait) to the container's thread, the UI thread.
            _inner = CreateWindowEx(WS_EX_NOPARENTNOTIFY, "static", "", WS_CHILD | WS_CLIPCHILDREN, 0, 0, r.Right, r.Bottom, _child, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_inner == IntPtr.Zero) throw new InvalidOperationException("Could not create the preview window.");
            var type = Type.GetTypeFromCLSID(_clsid, throwOnError: true)!;
            string? server = ServerProcessName(_clsid);
            var before = server == null ? [] : ProcessIds(server);
            instance = Activator.CreateInstance(type)!;
            if (server != null)
                foreach (int pid in ProcessIds(server).Except(before)) StartedServers.Add(pid);
            if (server != null) { _counted = true; _active++; }
            if (_closed) { ReleaseInstance(instance); return; }
            Initialize(instance);
            _handler = (IPreviewHandler)instance;
            _handler.SetWindow(_inner, ref r);
            _handler.DoPreview();
            KeepWarm(_clsid, type);
            if (_closed) { Release(); return; }
            SetWindowPos(_inner, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        catch (Exception ex)
        {
            Log.Information(ex, "Preview handler {Clsid} failed for {Path}", _clsid, _path);
            if (_handler == null && instance != null) ReleaseInstance(instance); // never became the handler
            Release();
            ui.BeginInvoke(() => Failed?.Invoke(ex));
        }
    }

    private void ReleaseInstance(object instance)
    {
        if (Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
        Release();
    }

    /// <summary>On the handler thread: the container was resized.</summary>
    private void Resize(RECT r)
    {
        if (_inner == IntPtr.Zero) return;
        SetWindowPos(_inner, IntPtr.Zero, 0, 0, r.Right, r.Bottom, SWP_NOZORDER | SWP_NOACTIVATE);
        try { _handler?.SetRect(ref r); } catch (COMException) { }
    }

    /// <summary>
    /// Handlers differ in what they accept (Word's claims IInitializeWithFile but answers E_NOTIMPL; others take
    /// only a stream or a shell item): try each one the handler offers, stream first as Explorer does.
    /// </summary>
    private void Initialize(object instance)
    {
        Exception? last = null;
        if (instance is IInitializeWithStream withStream)
        {
            try
            {
                int hr = SHCreateStreamOnFileEx(_path, STGM_READ | STGM_SHARE_DENY_NONE, 0, false, IntPtr.Zero, out _stream);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                withStream.Initialize(_stream, STGM_READ);
                return;
            }
            catch (Exception ex) when (ex is COMException or NotImplementedException or InvalidCastException) { last = ex; }
        }
        if (instance is IInitializeWithItem withItem)
        {
            try
            {
                withItem.Initialize((Desktop.ShellInterop.IShellItem)SHCreateItemFromParsingName(_path, IntPtr.Zero, Desktop.ShellInterop.IID_IShellItem), STGM_READ);
                return;
            }
            catch (Exception ex) when (ex is COMException or NotImplementedException or InvalidCastException) { last = ex; }
        }
        if (instance is IInitializeWithFile withFile)
        {
            try { withFile.Initialize(_path, STGM_READ); return; }
            catch (Exception ex) when (ex is COMException or NotImplementedException) { last = ex; }
        }
        throw last ?? new NotSupportedException("The preview handler cannot be initialised.");
    }

    /// <summary>Holds an idle instance of an out-of-process handler (and so its server) for a few minutes.</summary>
    private static void KeepWarm(Guid clsid, Type type)
    {
        using (var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid:B}\LocalServer32"))
            if (key == null) return; // in-process handlers start quickly anyway
        if (!Warm.ContainsKey(clsid))
        {
            try { Warm[clsid] = Activator.CreateInstance(type)!; }
            catch (Exception ex) when (ex is COMException or InvalidCastException) { return; }
        }
        if (_warmTimer == null)
        {
            _warmTimer = new System.Windows.Threading.DispatcherTimer { Interval = WarmFor };
            _warmTimer.Tick += (_, _) =>
            {
                _warmTimer!.Stop();
                foreach (var instance in Warm.Values)
                    try { Marshal.FinalReleaseComObject(instance); } catch (Exception ex) { Log.Debug(ex, "Releasing a warm preview handler failed"); }
                Warm.Clear();
                ScheduleCleanup();
            };
        }
        _warmTimer.Stop();
        _warmTimer.Start();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_child == IntPtr.Zero || (!_started && (sizeInfo.NewSize.Width <= 20 || sizeInfo.NewSize.Height <= 20))) return;
        bool start = !_started;
        _started = true;
        // After the container got its size (render priority), hand the size to the handler thread.
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed || _child == IntPtr.Zero) return;
            GetClientRect(_child, out RECT r);
            var ui = Dispatcher;
            if (start) HandlerThread.Value.BeginInvoke(() => Start(r, ui));
            else HandlerThread.Value.BeginInvoke(() => Resize(r));
        }, DispatcherPriority.Render);
    }

    /// <summary>On the handler thread: unloads the handler and frees its window.</summary>
    private void Release()
    {
        if (_handler != null)
        {
            try { _handler.Unload(); } catch (COMException) { }
            Marshal.FinalReleaseComObject(_handler);
            _handler = null;
        }
        if (_stream != null)
        {
            Marshal.FinalReleaseComObject(_stream);
            _stream = null;
        }
        if (_inner != IntPtr.Zero)
        {
            DestroyWindow(_inner);
            _inner = IntPtr.Zero;
        }
        if (_counted)
        {
            _counted = false;
            _active--;
            ScheduleCleanup();
        }
    }

    // ---- Office servers we started -------------------------------------------------------------------------

    /// <summary>The executable a COM server handler runs in ("WINWORD"), or null for an in-process handler.</summary>
    private static string? ServerProcessName(Guid clsid)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid:B}\LocalServer32");
        if (key?.GetValue(null) is not string command || command.Length == 0) return null;
        string exe = command.TrimStart('"');
        int end = exe.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return end < 0 ? null : System.IO.Path.GetFileNameWithoutExtension(exe[..(end + 4)]);
    }

    private static HashSet<int> ProcessIds(string name)
    {
        var ids = new HashSet<int>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(name)) { ids.Add(p.Id); p.Dispose(); }
        return ids;
    }

    /// <summary>A few seconds after the last preview and warm instance went (PowerPoint leaves by itself by then).</summary>
    private static void ScheduleCleanup()
    {
        if (StartedServers.Count == 0 || _active > 0 || Warm.Count > 0) return;
        if (_cleanupTimer == null)
        {
            _cleanupTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _cleanupTimer.Tick += (_, _) =>
            {
                _cleanupTimer!.Stop();
                if (_active == 0 && Warm.Count == 0) EndStartedServers();
            };
        }
        _cleanupTimer.Stop();
        _cleanupTimer.Start();
    }

    /// <summary>Ends the Office servers our previews started that are still running and show no window.</summary>
    private static void EndStartedServers()
    {
        foreach (int pid in StartedServers.ToList())
        {
            StartedServers.Remove(pid);
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                if (p.HasExited) continue;
                if (HasVisibleWindow(pid)) { Log.Information("Preview server {Name} ({Pid}) now shows a window; leaving it running", p.ProcessName, pid); continue; }
                p.Kill();
                Log.Information("Ended the preview server {Name} ({Pid}) our previews started", p.ProcessName, pid);
            }
            catch (ArgumentException) { } // already gone
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Log.Debug(ex, "Could not end preview server {Pid}", pid); }
        }
    }

    private static bool HasVisibleWindow(int pid)
    {
        bool found = false;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != pid || !IsWindowVisible(hwnd)) return true;
            found = true;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>When Chao Desk exits: let go of the warm instances and end the servers our previews started.</summary>
    public static void ShutDownServers()
    {
        if (!HandlerThread.IsValueCreated) return; // no preview handler was ever used
        try
        {
            HandlerThread.Value.Invoke(() =>
            {
                _warmTimer?.Stop();
                _cleanupTimer?.Stop();
                foreach (var instance in Warm.Values)
                    try { Marshal.FinalReleaseComObject(instance); } catch (Exception ex) { Log.Debug(ex, "Releasing a warm preview handler failed"); }
                Warm.Clear();
                EndStartedServers();
            }, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException) { Log.Information("The preview handler thread was busy at exit"); }
    }

    /// <summary>
    /// Hides the container at once (nothing waits for the handler thread); the handler thread then unloads the
    /// handler and frees its window, and only after that is the container destroyed.
    /// </summary>
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _closed = true;
        IntPtr container = hwnd.Handle;
        SetWindowPos(container, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW);
        _child = IntPtr.Zero;
        if (!HandlerThread.IsValueCreated) { DestroyWindow(container); return; }
        var ui = Dispatcher;
        HandlerThread.Value.BeginInvoke(() =>
        {
            Release();
            ui.BeginInvoke(() => DestroyWindow(container));
        });
    }

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT prc);
        void SetRect(ref RECT prc);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        [PreserveSig] uint TranslateAccelerator(ref Desktop.NativeMethods.MSG pmsg);
    }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream
    {
        void Initialize(IStream pstm, uint grfMode);
    }

    [ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithItem
    {
        void Initialize(Desktop.ShellInterop.IShellItem psi, uint grfMode);
    }
}
