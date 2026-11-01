using System.Runtime.InteropServices;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Input;

/// <summary>Consumes only the configured gesture. Work is dispatched after button-up, outside the hook.</summary>
public sealed class PanelMouseHook : IDisposable
{
    private readonly LowLevelMouseProc _callback;
    private readonly Action _trigger;
    private readonly Thread _thread;
    private volatile string _mode;
    private volatile bool _disposed;
    private uint _threadId, _swallowUp, _swallowData;
    private uint _firstTime;
    private POINT _firstPoint;
    private bool _fire;
    private IntPtr _hook;
    private Action<PointerPress>? _pressObserver;

    // Physical coordinates and the window hit at button-down remain useful even
    // if a popup closes or the panel starts moving before the UI dispatch runs.
    internal readonly record struct PointerPress(int X, int Y, IntPtr TargetWindow);
    internal void SetPressObserver(Action<PointerPress>? observer) => Volatile.Write(ref _pressObserver, observer);
    internal static bool IsObservablePress(uint message, uint flags) =>
        (flags & 1) == 0 && message is 0x201 or 0x204 or 0x207 or 0x20B;

    public PanelMouseHook(string mode, Action trigger)
    {
        _mode = mode; _trigger = trigger; _callback = Callback;
        _thread = new Thread(Run) { IsBackground = true, Name = "ChaoDesk.PanelMouse" }; _thread.Start();
    }

    public void SetMode(string mode) { _mode = mode; _firstTime = 0; }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) { Log.Error("Panel mouse hook failed: {Error}", Marshal.GetLastWin32Error()); return; }
        Log.Information("Panel mouse trigger active: {Mode}", _mode);
        try
        {
            while (!_disposed && GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        }
        finally { UnhookWindowsHookEx(_hook); }
    }

    private unsafe IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code != HC_ACTION) return CallNextHookEx(_hook, code, wParam, lParam);
        var mouse = (MSLLHOOKSTRUCT*)lParam;
        if ((mouse->flags & 1) != 0) return CallNextHookEx(_hook, code, wParam, lParam);
        uint message = (uint)wParam;
        bool sameButton = message is not (0x20B or 0x20C) || (mouse->mouseData >> 16) == _swallowData;
        if (_swallowUp != 0 && message == _swallowUp && sameButton)
        {
            _swallowUp = 0;
            if (_fire && !_disposed) { try { _trigger(); } catch (Exception ex) { Log.Warning(ex, "Could not dispatch panel mouse trigger"); } }
            _fire = false; return 1;
        }
        if (_swallowUp != 0 && message == _swallowUp - 1 && sameButton) return 1;
        string mode = _mode;
        bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        if (!_disposed && IsObservablePress(message,mouse->flags) &&
            PanelMouseTrigger.Matches(mode, message, mouse->mouseData, Down(VK_CONTROL), Down(VK_MENU), Down(VK_SHIFT), Down(VK_LWIN) || Down(VK_RWIN)))
        {
            _swallowUp = message + 1; _swallowData = mouse->mouseData >> 16; _fire = true;
            if (mode == "middleDouble")
            {
                _fire = _firstTime != 0 && unchecked(mouse->time - _firstTime) <= 450 &&
                    Math.Abs(mouse->pt.X - _firstPoint.X) <= 6 && Math.Abs(mouse->pt.Y - _firstPoint.Y) <= 6;
                _firstTime = _fire ? 0 : mouse->time; _firstPoint = mouse->pt;
            }
            else _firstTime = 0;
            return 1;
        }
        // Observe only ordinary presses, after the configured trigger has had
        // its chance to consume them. Never consume or synthesize outside input.
        if (!_disposed && IsObservablePress(message, mouse->flags) && Volatile.Read(ref _pressObserver) is { } observer)
        {
            IntPtr target = GetAncestor(WindowFromPoint(mouse->pt), 2); // GA_ROOT
            try { observer(new PointerPress(mouse->pt.X, mouse->pt.Y, target)); }
            catch (Exception ex) { Log.Warning(ex, "Could not dispatch panel pointer observation"); }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        _disposed = true;
        SetPressObserver(null);
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }
}
