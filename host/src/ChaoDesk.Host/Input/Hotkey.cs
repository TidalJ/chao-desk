using System.Windows.Input;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Input;

/// <summary>A key combination such as "Alt+Space" or "Shift+`". Parsing and formatting are pure (unit-tested).</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    // Keys whose enum names read badly in a settings field.
    private static readonly (Key Key, string Text)[] Names =
    [
        (Key.OemTilde, "`"), (Key.OemMinus, "-"), (Key.OemPlus, "="), (Key.OemOpenBrackets, "["), (Key.OemCloseBrackets, "]"),
        (Key.OemPipe, "\\"), (Key.OemSemicolon, ";"), (Key.OemQuotes, "'"), (Key.OemComma, ","), (Key.OemPeriod, "."),
        (Key.OemQuestion, "/"), (Key.Space, "Space"), (Key.Return, "Enter"), (Key.Back, "Backspace"), (Key.Escape, "Esc"),
    ];

    public bool IsEmpty => Key == Key.None;

    /// <summary>A modifier on its own, or a combination Windows reserves, cannot be a hotkey.</summary>
    public bool IsUsable =>
        !IsEmpty && Key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System)
        && (Modifiers != ModifierKeys.None || Key is >= Key.F1 and <= Key.F24);

    public uint NativeModifiers =>
        (Modifiers.HasFlag(ModifierKeys.Alt) ? MOD_ALT : 0) | (Modifiers.HasFlag(ModifierKeys.Control) ? MOD_CONTROL : 0) |
        (Modifiers.HasFlag(ModifierKeys.Shift) ? MOD_SHIFT : 0) | (Modifiers.HasFlag(ModifierKeys.Windows) ? MOD_WIN : 0);

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static readonly (Key Key, char Plain, char Shifted)[] Typed =
    [
        (Key.OemTilde, '`', '~'), (Key.OemMinus, '-', '_'), (Key.OemPlus, '=', '+'), (Key.OemOpenBrackets, '[', '{'),
        (Key.OemCloseBrackets, ']', '}'), (Key.OemPipe, '\\', '|'), (Key.OemSemicolon, ';', ':'), (Key.OemQuotes, '\'', '"'),
        (Key.OemComma, ',', '<'), (Key.OemPeriod, '.', '>'), (Key.OemQuestion, '/', '?'),
        (Key.D1, '1', '!'), (Key.D2, '2', '@'), (Key.D3, '3', '#'), (Key.D4, '4', '$'), (Key.D5, '5', '%'),
        (Key.D6, '6', '^'), (Key.D7, '7', '&'), (Key.D8, '8', '*'), (Key.D9, '9', '('), (Key.D0, '0', ')'),
    ];

    /// <summary>
    /// The character this combination would type if it were not a hotkey (Shift+` → "~", US layout), or null.
    /// Pressing such a hotkey again while its popup is open types the character instead.
    /// </summary>
    public string? TypedText()
    {
        if (Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return null;
        bool shift = Modifiers == ModifierKeys.Shift;
        foreach (var (k, plain, shifted) in Typed) if (k == Key) return (shift ? shifted : plain).ToString();
        if (Key is >= Key.A and <= Key.Z) return ((char)((shift ? 'A' : 'a') + (Key - Key.A))).ToString();
        return null;
    }

    public static string KeyName(Key key)
    {
        foreach (var (k, text) in Names) if (k == key) return text;
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        return key.ToString();
    }

    /// <summary>Parses "Alt+Space", "ctrl + shift + v", "Shift+~" (~ is the shifted ` key). Empty on failure.</summary>
    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        var mods = ModifierKeys.None;
        Key key = Key.None;
        // "+" on its own is the = key's shifted symbol; split on '+' but keep a trailing "+" as a key.
        var parts = text.Trim().EndsWith("++") ? text.Trim()[..^2].Split('+').Append("+") : text.Split('+');
        foreach (var raw in parts)
        {
            string p = raw.Trim();
            if (p.Length == 0) continue;
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; continue;
                case "alt": mods |= ModifierKeys.Alt; continue;
                case "shift": mods |= ModifierKeys.Shift; continue;
                case "win" or "windows": mods |= ModifierKeys.Windows; continue;
            }
            if (key != Key.None) return default; // two non-modifier keys
            key = ParseKey(p);
            if (key == Key.None) return default;
        }
        return new Hotkey(mods, key);
    }

    private static Key ParseKey(string p)
    {
        foreach (var (k, text) in Names) if (string.Equals(text, p, StringComparison.OrdinalIgnoreCase)) return k;
        switch (p)
        {
            case "~": return Key.OemTilde;
            case "+": return Key.OemPlus;
            case "_": return Key.OemMinus;
        }
        if (p.Length == 1 && char.IsDigit(p[0])) return Key.D0 + (p[0] - '0');
        return Enum.TryParse(p, ignoreCase: true, out Key parsed) && parsed != Key.None ? parsed : Key.None;
    }
}
