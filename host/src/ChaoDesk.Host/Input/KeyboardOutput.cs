using System.Runtime.InteropServices;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Input;

/// <summary>Synthesised keystrokes for pasting into, or typing into, the window the user came from.</summary>
public static class KeyboardOutput
{
    private static readonly ushort[] Modifiers = [VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN];
    internal static bool HavePressedModifiers => Modifiers.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    /// <summary>
    /// Waits (up to <paramref name="timeoutMs"/>) until Shift/Ctrl/Alt/Win are up, so a key the user still holds
    /// (Shift from Shift+Enter, say) does not turn our Ctrl+V into Ctrl+Shift+V.
    /// </summary>
    public static async Task WaitForModifiersReleasedAsync(int timeoutMs = 800)
    {
        long until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until && HavePressedModifiers)
            await Task.Delay(15);
    }

    public static void SendCtrlV() => SendUnchecked(Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true));

    public static void SendCtrlShiftT() => SendUnchecked(Key(VK_CONTROL, false), Key(VK_SHIFT, false), Key(0x54, false),
        Key(0x54, true), Key(VK_SHIFT, true), Key(VK_CONTROL, true));

    public static void SendShortcutChecked(string shortcut)
    {
        if (!PanelKeySequence.TryParse(shortcut, out var chord, out string error)) throw new ArgumentException(error, nameof(shortcut));
        SendShortcutChecked(chord);
    }

    internal static void SendShortcutChecked(KeyboardChord shortcut) => SendChecked(BuildShortcutInputs(shortcut));

    internal static INPUT[] BuildShortcutInputs(KeyboardChord shortcut)
    {
        var held = new List<ushort>();
        if ((shortcut.Modifiers & KeyboardModifiers.Control) != 0) held.Add(VK_CONTROL);
        if ((shortcut.Modifiers & KeyboardModifiers.Alt) != 0) held.Add(VK_MENU);
        if ((shortcut.Modifiers & KeyboardModifiers.Shift) != 0) held.Add(VK_SHIFT);
        if ((shortcut.Modifiers & KeyboardModifiers.Windows) != 0) held.Add(VK_LWIN);
        var inputs = held.Select(vk => Key(vk, false)).ToList();
        inputs.Add(Key(shortcut.VirtualKey, false));
        inputs.Add(Key(shortcut.VirtualKey, true));
        inputs.AddRange(held.AsEnumerable().Reverse().Select(vk => Key(vk, true)));
        return inputs.ToArray();
    }

    /// <summary>Types text as Unicode characters (independent of the keyboard layout and of any hotkey).</summary>
    public static void TypeText(string text)
    {
        SendUnchecked(BuildTextInputs(text));
    }

    public static void TypeTextChecked(string text) => SendChecked(BuildTextInputs(text));

    internal static INPUT[] BuildTextInputs(string text)
    {
        var inputs = new List<INPUT>();
        foreach (char c in text)
        {
            inputs.Add(Unicode(c, false));
            inputs.Add(Unicode(c, true));
        }
        return inputs.ToArray();
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? KEYEVENTF_KEYUP : 0) |
            (vk is >= 0x21 and <= 0x2E or VK_LWIN or VK_RWIN or 0x6F ? 0x0001u : 0) },
    };

    private static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) },
    };

    private static void SendUnchecked(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    private static void SendChecked(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        if (HavePressedModifiers) throw new InvalidOperationException("Release Ctrl, Alt, Shift and Windows before sending this action.");
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length) return;
        // A partial insertion must not leave a synthetic modifier held down.
        // Only release keys whose key-down was actually accepted by Windows.
        var release = BuildPartialReleaseInputs(inputs, (int)sent);
        if (release.Length > 0) SendInput((uint)release.Length, release, Marshal.SizeOf<INPUT>());
        throw new InvalidOperationException("Windows could not send input to the selected window. Its permissions may be higher than Chao Desk's.");
    }

    internal static INPUT[] BuildPartialReleaseInputs(INPUT[] inputs, int acceptedCount)
    {
        var held = new List<INPUT>();
        foreach (var input in inputs.Take(Math.Clamp(acceptedCount, 0, inputs.Length)))
        {
            bool SameKey(INPUT other) => input.ki.wVk == other.ki.wVk && input.ki.wScan == other.ki.wScan &&
                (input.ki.dwFlags & ~KEYEVENTF_KEYUP) == (other.ki.dwFlags & ~KEYEVENTF_KEYUP);
            if ((input.ki.dwFlags & KEYEVENTF_KEYUP) != 0)
            {
                int previous = held.FindLastIndex(SameKey);
                if (previous >= 0) held.RemoveAt(previous);
            }
            else if (!held.Exists(SameKey)) held.Add(input);
        }
        held.Reverse();
        var release = held.ToArray();
        for (int index = 0; index < release.Length; index++) release[index].ki.dwFlags |= KEYEVENTF_KEYUP;
        return release;
    }
}
