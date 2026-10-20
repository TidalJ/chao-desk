using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Preview;

/// <summary>
/// Hosts a Windows preview handler — the same component Explorer's preview pane uses (Word, Excel, PowerPoint,
/// Outlook messages, fonts, …, whatever the installed apps register) — in a child window.
/// </summary>
public sealed class PreviewHandlerHost : HwndHost
{
    private const string HandlerKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private const uint STGM_READ = 0;
    private const uint STGM_SHARE_DENY_NONE = 0x40;

    private readonly string _path;
    private readonly Guid _clsid;
    private IntPtr _child;
    private IPreviewHandler? _handler;
    private IStream? _stream;
    private bool _started;
    private bool _counted; // an out-of-process handler of ours is showing (counted in _active)

    public event Action<Exception>? Failed;

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

    private void Start()
    {
        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(_clsid, throwOnError: true)!;
            string? server = ServerProcessName(_clsid);
            var before = server == null ? [] : ProcessIds(server);
            instance = Activator.CreateInstance(type)!;
            if (server != null)
                foreach (int pid in ProcessIds(server).Except(before)) StartedServers.Add(pid);
            Initialize(instance);
            _handler = (IPreviewHandler)instance;
            GetClientRect(_child, out RECT r);
            _handler.SetWindow(_child, ref r);
            _handler.DoPreview();
            if (server != null) { _counted = true; _active++; }
            KeepWarm(_clsid, type);
        }
        catch (Exception ex)
        {
            Log.Information(ex, "Preview handler {Clsid} failed for {Path}", _clsid, _path);
            bool owned = _handler == null; // Release() frees the handler; an instance that never became one is ours
            Release();
            if (owned && instance != null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
            ScheduleCleanup();
            Failed?.Invoke(ex);
        }
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
        if (!_started && _child != IntPtr.Zero && sizeInfo.NewSize.Width > 20 && sizeInfo.NewSize.Height > 20)
        {
            _started = true;
            Dispatcher.BeginInvoke(Start, System.Windows.Threading.DispatcherPriority.Render); // after the child window got the size
            return;
        }
        if (_handler == null || _child == IntPtr.Zero) return;
        GetClientRect(_child, out RECT r);
        try { _handler.SetRect(ref r); } catch (COMException) { }
    }

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
        _warmTimer?.Stop();
        _cleanupTimer?.Stop();
        foreach (var instance in Warm.Values)
            try { Marshal.FinalReleaseComObject(instance); } catch (Exception ex) { Log.Debug(ex, "Releasing a warm preview handler failed"); }
        Warm.Clear();
        EndStartedServers();
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Release();
        DestroyWindow(hwnd.Handle);
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
