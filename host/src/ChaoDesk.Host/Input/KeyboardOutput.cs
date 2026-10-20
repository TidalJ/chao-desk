using System.Runtime.InteropServices;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Input;

/// <summary>Synthesised keystrokes for pasting into, or typing into, the window the user came from.</summary>
public static class KeyboardOutput
{
    private static readonly ushort[] Modifiers = [VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN];

    /// <summary>
    /// Waits (up to <paramref name="timeoutMs"/>) until Shift/Ctrl/Alt/Win are up, so a key the user still holds
    /// (Shift from Shift+Enter, say) does not turn our Ctrl+V into Ctrl+Shift+V.
    /// </summary>
    public static async Task WaitForModifiersReleasedAsync(int timeoutMs = 800)
    {
        long until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until && Modifiers.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0))
            await Task.Delay(15);
    }

    public static void SendCtrlV() => Send(Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true));

    /// <summary>Types text as Unicode characters (independent of the keyboard layout and of any hotkey).</summary>
    public static void TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (char c in text)
        {
            inputs.Add(Unicode(c, false));
            inputs.Add(Unicode(c, true));
        }
        Send(inputs.ToArray());
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 },
    };

    private static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) },
    };

    private static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
}
