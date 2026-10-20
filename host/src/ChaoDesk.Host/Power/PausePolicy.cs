using System.Runtime.InteropServices;
using System.Windows.Threading;
using ChaoDesk.Host.Desktop;
using Microsoft.Win32;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Power;

/// <summary>
/// Gathers pause inputs from Win32 (window state, power, session, display) and publishes a per-monitor
/// decision via <see cref="Changed"/>. Re-evaluates on WinEvents (debounced), power/session broadcasts,
/// and a 2-second fallback timer. UI thread only.
/// </summary>
public sealed class PausePolicy : IDisposable
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "SHELLDLL_DefView",
        "Windows.UI.Core.CoreWindow", // Start menu, search, Action Center flyouts
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow",
    };

    private readonly MessageWindow _window;
    private readonly Func<IReadOnlyList<MonitorInfo>> _monitors;
    private readonly Func<(bool pauseWhenMaximized, bool pauseOnBattery)> _options;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _fallback;
    private readonly WinEventProc _winEventProc; // keep the delegate alive while hooks are installed
    private readonly List<IntPtr> _winEventHooks = new();
    private readonly List<IntPtr> _powerNotifications = new();
    private readonly List<WindowSnapshot> _windowsScratch = new();
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    private bool _sessionLocked;
    private bool _displayOff;
    private bool _suspended;
    private bool _batterySaver;
    private Dictionary<string, PauseReason> _last = new();

    public IReadOnlyDictionary<string, PauseReason> Current => _last;

    /// <summary>Raised when any monitor's pause reasons change.</summary>
    public event Action<IReadOnlyDictionary<string, PauseReason>>? Changed;

    public PausePolicy(MessageWindow window, Func<IReadOnlyList<MonitorInfo>> monitors,
        Func<(bool pauseWhenMaximized, bool pauseOnBattery)> options)
    {
        _window = window;
        _monitors = monitors;
        _options = options;
        _window.Message += OnMessage;

        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Evaluate(); };

        _fallback = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _fallback.Tick += (_, _) => Evaluate();

        _winEventProc = OnWinEvent;
        HookWinEvent(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        HookWinEvent(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        HookWinEvent(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE);

        RegisterPower(GUID_CONSOLE_DISPLAY_STATE);
        RegisterPower(GUID_POWER_SAVING_STATUS);
        WTSRegisterSessionNotification(_window.Handle, NOTIFY_FOR_THIS_SESSION);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _batterySaver = ReadPowerStatus().batterySaver;
        _fallback.Start();
    }

    /// <summary>Request a re-evaluation soon (coalesces bursts of events).</summary>
    public void Invalidate()
    {
        if (!_debounce.IsEnabled) _debounce.Start();
    }

    public void Evaluate()
    {
        var monitors = _monitors();
        var (pauseMax, pauseBattery) = _options();
        var (onBattery, saver) = ReadPowerStatus();
        _batterySaver = saver;

        CollectWindows(_windowsScratch);
        bool d3d = SHQueryUserNotificationState(out var quns) == 0 &&
                   quns is UserNotificationState.RunningD3DFullScreen or UserNotificationState.PresentationMode;

        var input = new PauseInputs(monitors, _windowsScratch, d3d, onBattery, _batterySaver, _sessionLocked,
            _displayOff, _suspended, pauseMax, pauseBattery);
        var result = PauseEvaluator.Evaluate(input);

        if (!SameAs(result, _last))
        {
            _last = result;
            foreach (var kv in result) Log.Information("Pause state {Monitor}: {Reasons}", kv.Key, kv.Value);
            Changed?.Invoke(result);
        }
    }

    private void CollectWindows(List<WindowSnapshot> list)
    {
        list.Clear();
        IntPtr fg = GetForegroundWindow();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == _ownPid) return true;

            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            bool isForeground = hwnd == fg;
            // Transparent overlays (e.g. GPU overlays) and tool windows never count, unless focused.
            if (!isForeground && (ex & (WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return true;

            GetWindowRect(hwnd, out RECT r);
            var bounds = PixelRect.From(r);
            if (bounds.Width <= 0 || bounds.Height <= 0) return true;

            bool shell = ShellClasses.Contains(GetClassNameOf(hwnd));
            list.Add(new WindowSnapshot(bounds, IsZoomed(hwnd), isForeground, shell));
            return true;
        }, IntPtr.Zero);
    }

    private static (bool onBattery, bool batterySaver) ReadPowerStatus()
    {
        if (!GetSystemPowerStatus(out var s)) return (false, false);
        bool hasBattery = s.BatteryFlag != 128 && s.BatteryFlag != 255;
        return (hasBattery && s.ACLineStatus == 0, s.SystemStatusFlag == 1);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Keep this cheap: LOCATIONCHANGE fires for every caret/cursor move system-wide.
        if (eventType == EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (idObject != OBJID_WINDOW || idChild != 0 || hwnd != GetForegroundWindow()) return;
        }
        Invalidate();
    }

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WTSSESSION_CHANGE)
        {
            int code = wParam.ToInt32();
            if (code == WTS_SESSION_LOCK) { _sessionLocked = true; Log.Information("Session locked"); Evaluate(); }
            else if (code == WTS_SESSION_UNLOCK) { _sessionLocked = false; Log.Information("Session unlocked"); Evaluate(); }
        }
        else if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
            {
                _displayOff = setting.Data == 0; // 0 = off, 1 = on, 2 = dimmed
                Log.Information("Display state: {State}", setting.Data switch { 0 => "off", 1 => "on", _ => "dimmed" });
            }
            else if (setting.PowerSetting == GUID_POWER_SAVING_STATUS)
            {
                _batterySaver = setting.Data != 0;
                Log.Information("Battery saver: {State}", _batterySaver ? "on" : "off");
            }
            Evaluate();
        }
        else if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_APMPOWERSTATUSCHANGE)
        {
            Evaluate();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // SystemEvents may raise on its own thread; marshal to the UI thread.
        _window.Dispatch(() =>
        {
            if (e.Mode == PowerModes.Suspend) { _suspended = true; Log.Information("System suspending"); }
            else if (e.Mode == PowerModes.Resume) { _suspended = false; Log.Information("System resumed"); }
            Evaluate();
        });
    }

    private void HookWinEvent(uint min, uint max)
    {
        IntPtr h = SetWinEventHook(min, max, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (h != IntPtr.Zero) _winEventHooks.Add(h);
        else Log.Warning("SetWinEventHook({Min:X}) failed", min);
    }

    private void RegisterPower(Guid guid)
    {
        IntPtr h = RegisterPowerSettingNotification(_window.Handle, ref guid, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        if (h != IntPtr.Zero) _powerNotifications.Add(h);
    }

    private static bool SameAs(Dictionary<string, PauseReason> a, Dictionary<string, PauseReason> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
        return true;
    }

    public void Dispose()
    {
        _debounce.Stop();
        _fallback.Stop();
        foreach (var h in _winEventHooks) UnhookWinEvent(h);
        _winEventHooks.Clear();
        foreach (var h in _powerNotifications) UnregisterPowerSettingNotification(h);
        _powerNotifications.Clear();
        WTSUnRegisterSessionNotification(_window.Handle);
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _window.Message -= OnMessage;
    }
}
