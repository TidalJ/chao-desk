using System.Diagnostics.CodeAnalysis;

namespace ChaoDesk.Host.Input;

[Flags]
internal enum KeyboardModifiers { None = 0, Control = 1, Alt = 2, Shift = 4, Windows = 8 }

internal sealed record KeyboardChord(ushort VirtualKey, KeyboardModifiers Modifiers, string DisplayText);

/// <summary>The single-chord grammar shared by action validation and input output.</summary>
internal static class PanelKeySequence
{
    internal static bool TryParse(string? input, [NotNullWhen(true)] out KeyboardChord? chord, out string error)
    {
        chord = null;
        error = "Enter one key or a combination such as Ctrl+Shift+T.";
        if (string.IsNullOrWhiteSpace(input)) return false;
        string[] parts = input.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrEmpty)) return false;
        KeyboardModifiers modifiers = KeyboardModifiers.None;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            KeyboardModifiers modifier = parts[index].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => KeyboardModifiers.Control,
                "ALT" => KeyboardModifiers.Alt,
                "SHIFT" => KeyboardModifiers.Shift,
                "WIN" or "WINDOWS" => KeyboardModifiers.Windows,
                _ => KeyboardModifiers.None
            };
            if (modifier == KeyboardModifiers.None || (modifiers & modifier) != 0) return false;
            modifiers |= modifier;
        }

        string key = parts[^1].ToUpperInvariant();
        ushort virtualKey;
        string name;
        if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9'))
        { virtualKey = key[0]; name = key; }
        else if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out int number) && number is >= 1 and <= 24 && key == "F" + number)
        { virtualKey = (ushort)(0x70 + number - 1); name = key; }
        else if (key.StartsWith("NUMPAD", StringComparison.Ordinal) && key.Length == 7 && key[6] is >= '0' and <= '9')
        { virtualKey = (ushort)(0x60 + key[6] - '0'); name = "NumPad" + key[6]; }
        else
        {
            var mapped = key switch
            {
                "ENTER" or "RETURN" => (0x0D, "Enter"),
                "TAB" => (0x09, "Tab"),
                "SPACE" => (0x20, "Space"),
                "ESC" or "ESCAPE" => (0x1B, "Esc"),
                "DELETE" or "DEL" => (0x2E, "Delete"),
                "LEFT" or "ARROWLEFT" => (0x25, "Left"),
                "UP" or "ARROWUP" => (0x26, "Up"),
                "RIGHT" or "ARROWRIGHT" => (0x27, "Right"),
                "DOWN" or "ARROWDOWN" => (0x28, "Down"),
                "HOME" => (0x24, "Home"),
                "END" => (0x23, "End"),
                "PAGEUP" or "PGUP" => (0x21, "PageUp"),
                "PAGEDOWN" or "PGDN" => (0x22, "PageDown"),
                "INSERT" or "INS" => (0x2D, "Insert"),
                "BACKSPACE" => (0x08, "Backspace"),
                "CAPSLOCK" => (0x14, "CapsLock"),
                "PLUS" or "OEMPLUS" => (0xBB, "Plus"),
                "MINUS" or "OEMMINUS" => (0xBD, "Minus"),
                "ADD" or "NUMPADADD" => (0x6B, "NumPadAdd"),
                "SUBTRACT" or "NUMPADSUBTRACT" => (0x6D, "NumPadSubtract"),
                "MULTIPLY" or "NUMPADMULTIPLY" => (0x6A, "NumPadMultiply"),
                "DIVIDE" or "NUMPADDIVIDE" => (0x6F, "NumPadDivide"),
                "DECIMAL" or "NUMPADDECIMAL" => (0x6E, "NumPadDecimal"),
                _ => (0, "")
            };
            virtualKey = (ushort)mapped.Item1;
            name = mapped.Item2;
            if (virtualKey == 0) return false;
        }

        var display = new List<string>();
        if ((modifiers & KeyboardModifiers.Control) != 0) display.Add("Ctrl");
        if ((modifiers & KeyboardModifiers.Alt) != 0) display.Add("Alt");
        if ((modifiers & KeyboardModifiers.Shift) != 0) display.Add("Shift");
        if ((modifiers & KeyboardModifiers.Windows) != 0) display.Add("Win");
        display.Add(name);
        chord = new KeyboardChord(virtualKey, modifiers, string.Join('+', display));
        error = "";
        return true;
    }
}
