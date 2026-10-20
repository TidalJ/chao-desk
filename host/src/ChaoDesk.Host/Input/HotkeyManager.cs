using System.Runtime.InteropServices;
using ChaoDesk.Host.Desktop;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Input;

/// <summary>
/// System-wide hotkeys via RegisterHotKey on the hidden message window. A registered combination is consumed
/// before any window sees it, which is what makes Alt+Space (normally the window menu) usable. UI thread only.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly MessageWindow _window;
    private readonly Dictionary<int, (Hotkey Key, Action Action)> _registered = new();
    private int _nextId = 1;

    public HotkeyManager(MessageWindow window)
    {
        _window = window;
        _window.Message += OnMessage;
    }

    /// <summary>Registers <paramref name="key"/>; returns an id for <see cref="Unregister"/>, or 0 if Windows refused.</summary>
    public int Register(Hotkey key, Action action)
    {
        if (!key.IsUsable) return 0;
        int id = _nextId++;
        if (!RegisterHotKey(_window.Handle, id, key.NativeModifiers | MOD_NOREPEAT, key.VirtualKey))
        {
            Log.Warning("Could not register hotkey {Hotkey} (error {Error}); another app probably uses it", key, Marshal.GetLastWin32Error());
            return 0;
        }
        _registered[id] = (key, action);
        Log.Information("Registered hotkey {Hotkey}", key);
        return id;
    }

    public void Unregister(int id)
    {
        if (id == 0 || !_registered.Remove(id)) return;
        UnregisterHotKey(_window.Handle, id);
    }

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY || !_registered.TryGetValue((int)wParam, out var entry)) return;
        handled = true;
        try { entry.Action(); }
        catch (Exception ex) { Log.Error(ex, "Hotkey {Hotkey} handler failed", entry.Key); }
    }

    public void Dispose()
    {
        foreach (int id in _registered.Keys) UnregisterHotKey(_window.Handle, id);
        _registered.Clear();
        _window.Message -= OnMessage;
    }
}
