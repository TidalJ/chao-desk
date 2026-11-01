using ChaoDesk.Host.Input;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Tests;

public sealed class PanelKeySequenceTests
{
    [Theory]
    [InlineData(" control + shift + t ", "Ctrl+Shift+T", 0x54)]
    [InlineData("Win+Alt+Left", "Alt+Win+Left", 0x25)]
    [InlineData("Ctrl+0", "Ctrl+0", 0x30)]
    [InlineData("F1", "F1", 0x70)]
    [InlineData("Ctrl+F24", "Ctrl+F24", 0x87)]
    [InlineData("Escape", "Esc", 0x1B)]
    [InlineData("Return", "Enter", 0x0D)]
    [InlineData("Tab", "Tab", 0x09)]
    [InlineData("Space", "Space", 0x20)]
    [InlineData("CapsLock", "CapsLock", 0x14)]
    [InlineData("Ctrl+OemPlus", "Ctrl+Plus", 0xBB)]
    [InlineData("Alt+Minus", "Alt+Minus", 0xBD)]
    [InlineData("NumPad0", "NumPad0", 0x60)]
    [InlineData("NumPad9", "NumPad9", 0x69)]
    [InlineData("Divide", "NumPadDivide", 0x6F)]
    public void ParserAcceptsOnlyOneCanonicalChord(string input, string display, int key)
    {
        Assert.True(PanelKeySequence.TryParse(input, out var chord, out string error), error);
        Assert.Equal(display, chord!.DisplayText);
        Assert.Equal(key, chord.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Control+A")]
    [InlineData("Ctrl++A")]
    [InlineData("A+B")]
    [InlineData("Ctrl+A,Ctrl+C")]
    [InlineData("Ctrl+A\r\nCtrl+C")]
    [InlineData("Super+A")]
    [InlineData("F0")]
    [InlineData("F25")]
    [InlineData("F01")]
    public void InvalidOrMultiChordInputCannotReachSendInput(string input)
    {
        Assert.False(PanelKeySequence.TryParse(input, out var chord, out string error));
        Assert.Null(chord);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ModifiersPressInOrderAndReleaseInReverseOrder()
    {
        Assert.True(PanelKeySequence.TryParse("Ctrl+Alt+Shift+Win+Delete", out var chord, out _));
        var inputs = KeyboardOutput.BuildShortcutInputs(chord!);
        ushort[] expected = [VK_CONTROL, VK_MENU, VK_SHIFT, VK_LWIN, 0x2E, 0x2E, VK_LWIN, VK_SHIFT, VK_MENU, VK_CONTROL];
        Assert.Equal(expected, inputs.Select(input => input.ki.wVk));
        Assert.All(inputs.Take(5), input => Assert.Equal(0u, input.ki.dwFlags & KEYEVENTF_KEYUP));
        Assert.All(inputs.Skip(5), input => Assert.Equal(KEYEVENTF_KEYUP, input.ki.dwFlags & KEYEVENTF_KEYUP));
        Assert.All(inputs, input => Assert.Equal(INPUT_KEYBOARD, input.type));
        Assert.All(inputs.Where(input => input.ki.wVk is VK_LWIN or 0x2E), input => Assert.Equal(1u, input.ki.dwFlags & 1));
        Assert.All(inputs.Where(input => input.ki.wVk is VK_CONTROL or VK_SHIFT or VK_MENU), input => Assert.Equal(0u, input.ki.dwFlags & 1));
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("Up")]
    [InlineData("Down")]
    [InlineData("Home")]
    [InlineData("End")]
    [InlineData("Insert")]
    [InlineData("PageUp")]
    [InlineData("PageDown")]
    [InlineData("Delete")]
    [InlineData("NumPadDivide")]
    public void NavigationKeysUseExtendedInputFlags(string key)
    {
        PanelKeySequence.TryParse(key, out var chord, out _);
        Assert.All(KeyboardOutput.BuildShortcutInputs(chord!), input => Assert.Equal(1u, input.ki.dwFlags & 1));
    }

    [Fact]
    public void TextPreservesUnicodeSurrogatesWhitespaceAndNewlinesWithoutExtraEnter()
    {
        string text = "  你好 😀\r\n\tend ";
        var inputs = KeyboardOutput.BuildTextInputs(text);
        Assert.Equal(text.Length * 2, inputs.Length);
        Assert.Equal(text, new string(inputs.Where((_, index) => index % 2 == 0).Select(input => (char)input.ki.wScan).ToArray()));
        Assert.All(inputs, input => Assert.Equal(0, input.ki.wVk));
        Assert.All(inputs.Where((_, index) => index % 2 == 0), input => Assert.Equal(KEYEVENTF_UNICODE, input.ki.dwFlags));
        Assert.All(inputs.Where((_, index) => index % 2 == 1), input => Assert.Equal(KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, input.ki.dwFlags));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "17")]
    [InlineData(3, "84,16,17")]
    [InlineData(4, "16,17")]
    [InlineData(5, "17")]
    [InlineData(6, "")]
    public void PartialFailureReleasesOnlyUnmatchedAcceptedKeyDowns(int count, string keys)
    {
        PanelKeySequence.TryParse("Ctrl+Shift+T", out var chord, out _);
        var cleanup = KeyboardOutput.BuildPartialReleaseInputs(KeyboardOutput.BuildShortcutInputs(chord!), count);
        Assert.Equal(keys, string.Join(',', cleanup.Select(input => input.ki.wVk)));
        Assert.All(cleanup, input => Assert.Equal(KEYEVENTF_KEYUP, input.ki.dwFlags & KEYEVENTF_KEYUP));
    }

    [Fact]
    public void PartialTextFailureNeverReleasesAnAlreadyCompletedCharacterAgain()
    {
        var inputs = KeyboardOutput.BuildTextInputs("ab");
        Assert.Empty(KeyboardOutput.BuildPartialReleaseInputs(inputs, 2));
        var cleanup = Assert.Single(KeyboardOutput.BuildPartialReleaseInputs(inputs, 3));
        Assert.Equal('b', cleanup.ki.wScan);
        Assert.Equal(KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, cleanup.ki.dwFlags);
    }
}
