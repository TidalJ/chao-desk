using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using ChaoDesk.Host.Desktop;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Input;

public enum PointerEventKind { Move, Down, Up, Click }

public enum PointerButton { None, Left, Right, Middle }

/// <summary>A pointer event in physical screen pixels.</summary>
public readonly record struct DesktopPointerEvent(PointerEventKind Kind, int X, int Y, PointerButton Button);

/// <summary>
/// Forwards mouse input over the desktop to the wallpaper.
///
/// Threads:
///  * hook thread — owns the WH_MOUSE_LL hook and a message loop. The callback only copies the
///    event into a queue (or the "latest move" slot) and signals; it never blocks.
///  * worker thread — decides whether the cursor is over the desktop, throttles moves to 60/s,
///    synthesises clicks, and hands results to <see cref="_sink"/> (which marshals to the UI thread).
/// Disabling interaction disposes this object, which removes the hook entirely.
/// </summary>
public sealed class MouseForwarder : IDisposable
{
    public const int ClickSlopPx = 5;
    public const int ClickMaxMs = 300;
    private const int MoveIntervalMs = 1000 / 60;

    private static readonly HashSet<string> DesktopClasses = new(StringComparer.Ordinal)
    {
        "SysListView32", "SHELLDLL_DefView", "WorkerW", "Progman",
    };

    private readonly Action<DesktopPointerEvent> _sink;
    private readonly Func<bool> _anyRunning;
    private readonly Thread _hookThread;
    private readonly Thread _workerThread;
    private readonly AutoResetEvent _signal = new(false);
    private readonly ConcurrentQueue<RawEvent> _buttons = new();
    private readonly LowLevelMouseProc _hookProc; // keep alive
    private readonly CancellationTokenSource _cts = new();
    private uint _hookThreadId;
    private IntPtr _hook;

    // Latest move, written by the hook thread. Packed into one long so reads are atomic.
    private long _latestMove;
    private int _moveDirty;

    private readonly ClickSynthesizer _clicks = new(ClickSlopPx, ClickMaxMs);
    private PointerButton _forwardedDown = PointerButton.None;
    private IntPtr _lastHwnd;
    private bool _lastHwndIsDesktop;

    private readonly record struct RawEvent(uint Message, int X, int Y, uint Time, bool Captured = false);

    // Screen areas (physical px) where the wallpaper shows its own controls, set by the page. A left
    // press there, over the desktop, is kept from Explorer (so dragging the card never draws the
    // selection rectangle and clicking it never deselects icons) and still forwarded to the page.
    private PixelRect[] _captureRects = [];
    private bool _swallowUp; // hook thread only

    public void SetCaptureRects(IReadOnlyCollection<PixelRect> rects) => Volatile.Write(ref _captureRects, rects.ToArray());

    public MouseForwarder(Action<DesktopPointerEvent> sink, Func<bool> anyRunning)
    {
        _sink = sink;
        _anyRunning = anyRunning;
        _hookProc = HookCallback;

        _hookThread = new Thread(HookThreadMain) { IsBackground = true, Name = "ChaoDesk.MouseHook", Priority = ThreadPriority.AboveNormal };
        _workerThread = new Thread(WorkerMain) { IsBackground = true, Name = "ChaoDesk.MouseWorker" };
        _hookThread.Start();
        _workerThread.Start();
    }

    // ---- Hook thread ------------------------------------------------------------------------------

    private void HookThreadMain()
    {
        // The hook thread must be Per-Monitor V2 aware so MSLLHOOKSTRUCT.pt is in physical pixels.
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        _hookThreadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Error("SetWindowsHookEx(WH_MOUSE_LL) failed ({Error})", Marshal.GetLastWin32Error());
            return;
        }
        Log.Information("Mouse hook installed");

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        Log.Information("Mouse hook removed");
    }

    private unsafe IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION)
        {
            uint msg = (uint)wParam;
            var data = (MSLLHOOKSTRUCT*)lParam;
            switch (msg)
            {
                case WM_MOUSEMOVE:
                    Volatile.Write(ref _latestMove, Pack(data->pt.X, data->pt.Y));
                    if (Interlocked.Exchange(ref _moveDirty, 1) == 0) _signal.Set();
                    break;
                case WM_LBUTTONDOWN when InCaptureRect(data->pt.X, data->pt.Y) && IsDesktopWindowAt(data->pt.X, data->pt.Y):
                    _swallowUp = true;
                    _buttons.Enqueue(new RawEvent(msg, data->pt.X, data->pt.Y, data->time, Captured: true));
                    _signal.Set();
                    return 1;
                case WM_LBUTTONUP when _swallowUp:
                    _swallowUp = false;
                    _buttons.Enqueue(new RawEvent(msg, data->pt.X, data->pt.Y, data->time, Captured: true));
                    _signal.Set();
                    return 1;
                case WM_LBUTTONDOWN or WM_LBUTTONUP or WM_RBUTTONDOWN or WM_RBUTTONUP or WM_MBUTTONDOWN or WM_MBUTTONUP:
                    _buttons.Enqueue(new RawEvent(msg, data->pt.X, data->pt.Y, data->time));
                    _signal.Set();
                    break;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool InCaptureRect(int x, int y)
    {
        foreach (var r in Volatile.Read(ref _captureRects)) if (r.Contains(x, y)) return true;
        return false;
    }

    /// <summary>Uncached desktop test for the hook thread (only called inside a capture rect).</summary>
    private static bool IsDesktopWindowAt(int x, int y)
    {
        for (IntPtr h = WindowFromPoint(new POINT(x, y)); h != IntPtr.Zero; h = GetAncestor(h, GA_PARENT))
        {
            string cls = GetClassNameOf(h);
            if (DesktopClasses.Contains(cls) || cls == "ChaoDeskWallpaperWindow") return true;
            if (h == GetDesktopWindow()) break;
        }
        return false;
    }

    private static long Pack(int x, int y) => ((long)x << 32) | (uint)y;
    private static (int x, int y) Unpack(long v) => ((int)(v >> 32), (int)v);

    // ---- Worker thread ----------------------------------------------------------------------------

    private void WorkerMain()
    {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        long lastMoveTick = 0;
        int waitMs = Timeout.Infinite;
        var token = _cts.Token;

        while (!token.IsCancellationRequested)
        {
            _signal.WaitOne(waitMs);
            if (token.IsCancellationRequested) break;
            waitMs = Timeout.Infinite;

            while (_buttons.TryDequeue(out var e)) HandleButton(e);

            if (Volatile.Read(ref _moveDirty) == 1)
            {
                long now = Environment.TickCount64;
                long due = lastMoveTick + MoveIntervalMs;
                if (now < due)
                {
                    waitMs = (int)(due - now); // throttle: come back when the interval has elapsed
                    continue;
                }
                Interlocked.Exchange(ref _moveDirty, 0);
                var (x, y) = Unpack(Volatile.Read(ref _latestMove));
                HandleMove(x, y);
                lastMoveTick = now;
            }
        }
    }

    private void HandleMove(int x, int y)
    {
        _clicks.OnMove(x, y);
        // Keep moves flowing to the page while a forwarded button is held (drag), otherwise only over the desktop.
        if (_forwardedDown == PointerButton.None && (!_anyRunning() || !IsDesktopAt(x, y))) return;
        _sink(new DesktopPointerEvent(PointerEventKind.Move, x, y, PointerButton.None));
    }

    private void HandleButton(RawEvent e)
    {
        var button = e.Message switch
        {
            WM_LBUTTONDOWN or WM_LBUTTONUP => PointerButton.Left,
            WM_RBUTTONDOWN or WM_RBUTTONUP => PointerButton.Right,
            _ => PointerButton.Middle,
        };
        bool down = e.Message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN;

        if (down)
        {
            if (!e.Captured && (!IsDesktopAt(e.X, e.Y, forceRefresh: true) || IsOverDesktopIcon(e.X, e.Y)))
            {
                _clicks.Cancel();
                return;
            }
            _forwardedDown = button;
            _clicks.OnDown(e.X, e.Y, e.Time, button);
            _sink(new DesktopPointerEvent(PointerEventKind.Down, e.X, e.Y, button));
        }
        else
        {
            if (_forwardedDown != button) return;
            _forwardedDown = PointerButton.None;
            _sink(new DesktopPointerEvent(PointerEventKind.Up, e.X, e.Y, button));
            if (_clicks.OnUp(e.X, e.Y, e.Time, button))
                _sink(new DesktopPointerEvent(PointerEventKind.Click, e.X, e.Y, button));
        }
    }

    /// <summary>The window under the point, or one of its ancestors, is part of the desktop.</summary>
    private bool IsDesktopAt(int x, int y, bool forceRefresh = false)
    {
        IntPtr hwnd = WindowFromPoint(new POINT(x, y));
        if (!forceRefresh && hwnd == _lastHwnd) return _lastHwndIsDesktop;

        bool result = false;
        for (IntPtr h = hwnd; h != IntPtr.Zero; h = GetAncestor(h, GA_PARENT))
        {
            string cls = GetClassNameOf(h);
            if (DesktopClasses.Contains(cls) || cls == "ChaoDeskWallpaperWindow") { result = true; break; }
            if (h == GetDesktopWindow()) break;
        }
        _lastHwnd = hwnd;
        _lastHwndIsDesktop = result;
        return result;
    }

    /// <summary>
    /// True if the cursor is over a desktop icon. The desktop list view hot-tracks the item under the
    /// cursor, so LVM_GETHOTITEM answers this without cross-process memory.
    /// </summary>
    private static bool IsOverDesktopIcon(int x, int y)
    {
        IntPtr hwnd = WindowFromPoint(new POINT(x, y));
        if (GetClassNameOf(hwnd) != "SysListView32") return false;
        if (SendMessageTimeout(hwnd, LVM_GETHOTITEM, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 50, out IntPtr result) == IntPtr.Zero)
            return false;
        return result.ToInt64() >= 0;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _signal.Set();
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread.Join(1000);
        _workerThread.Join(1000);
        _signal.Dispose();
        _cts.Dispose();
    }
}

/// <summary>Down/up within a small distance and time window → click. Pure logic, unit-tested.</summary>
public sealed class ClickSynthesizer
{
    private readonly int _slop;
    private readonly uint _maxMs;
    private bool _armed;
    private int _x, _y;
    private uint _time;
    private PointerButton _button;

    public ClickSynthesizer(int slopPx, int maxMs)
    {
        _slop = slopPx;
        _maxMs = (uint)maxMs;
    }

    public void OnDown(int x, int y, uint time, PointerButton button)
    {
        _armed = true;
        _x = x; _y = y; _time = time; _button = button;
    }

    public void OnMove(int x, int y)
    {
        if (_armed && !Within(x, y)) _armed = false; // dragging (e.g. rubber-band icon selection)
    }

    public bool OnUp(int x, int y, uint time, PointerButton button)
    {
        bool click = _armed && button == _button && Within(x, y) && unchecked(time - _time) <= _maxMs;
        _armed = false;
        return click;
    }

    public void Cancel() => _armed = false;

    private bool Within(int x, int y) => Math.Abs(x - _x) <= _slop && Math.Abs(y - _y) <= _slop;
}
