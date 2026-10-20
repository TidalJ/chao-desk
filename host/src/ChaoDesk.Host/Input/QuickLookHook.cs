using System.Runtime.InteropServices;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Input;

/// <summary>
/// Quick Look's trigger: a low-level keyboard hook that takes a plain Space press only when Explorer's file list
/// (or the desktop's) has the keyboard focus — never while renaming, typing in the address or search box, or in
/// any other app. The callback decides with three cheap calls and hands the window to <c>sink</c>; it never blocks.
/// </summary>
public sealed class QuickLookHook : IDisposable
{
    private const uint VK_SPACE = 0x20;
    private static readonly HashSet<string> ExplorerWindows = new(StringComparer.Ordinal) { "CabinetWClass", "ExploreWClass", "Progman", "WorkerW" };
    private static readonly HashSet<string> FileLists = new(StringComparer.Ordinal) { "DirectUIHWND", "SysListView32" };

    private readonly Action<IntPtr> _sink;
    private readonly LowLevelKeyboardProc _proc; // keep alive
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _hook;
    private bool _swallowUp;

    public QuickLookHook(Action<IntPtr> sink)
    {
        _sink = sink;
        _proc = Callback;
        _thread = new Thread(Run) { IsBackground = true, Name = "ChaoDesk.QuickLookHook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Error("SetWindowsHookEx(WH_KEYBOARD_LL) failed ({Error})", Marshal.GetLastWin32Error());
            return;
        }
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UnhookWindowsHookEx(_hook);
    }

    private unsafe IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION)
        {
            var k = (KBDLLHOOKSTRUCT*)lParam;
            if (k->vkCode == VK_SPACE)
            {
                uint msg = (uint)wParam;
                if (msg == WM_KEYUP && _swallowUp) { _swallowUp = false; return 1; }
                if (msg == WM_KEYDOWN && (k->flags & LLKHF_INJECTED) == 0 && !AnyModifierDown() && FileListFocused(out IntPtr window))
                {
                    _swallowUp = true;
                    _sink(window);
                    return 1;
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static readonly ushort[] Modifiers = [VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN];

    private static bool AnyModifierDown()
    {
        foreach (ushort vk in Modifiers)
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        return false;
    }

    private static bool FileListFocused(out IntPtr window)
    {
        window = GetForegroundWindow();
        if (window == IntPtr.Zero || !ExplorerWindows.Contains(GetClassNameOf(window))) return false;
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        uint thread = GetWindowThreadProcessId(window, out _);
        return GetGUIThreadInfo(thread, ref info) && info.hwndFocus != IntPtr.Zero && FileLists.Contains(GetClassNameOf(info.hwndFocus));
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }
}
