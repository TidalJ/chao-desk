namespace ChaoDesk.Host.Input;

/// <summary>Stable persisted names and a pure matcher shared by the hook and tests.</summary>
public static class PanelMouseTrigger
{
    public static readonly string[] Modes = ["none", "middle", "middleDouble", "x1", "x2", "ctrlRight", "altLeft"];

    public static bool Matches(string mode, uint message, uint mouseData, bool ctrl, bool alt, bool shift, bool win)
    {
        if (shift || win) return false;
        return mode switch
        {
            "middle" or "middleDouble" => message == 0x207 && !ctrl && !alt,
            "x1" => message == 0x20B && (mouseData >> 16) == 1 && !ctrl && !alt,
            "x2" => message == 0x20B && (mouseData >> 16) == 2 && !ctrl && !alt,
            "ctrlRight" => message == 0x204 && ctrl && !alt,
            "altLeft" => message == 0x201 && alt && !ctrl,
            _ => false,
        };
    }
}
