using ChaoDesk.Host.Clips;

namespace ChaoDesk.Host.Settings;

/// <summary>
/// What the home page shows: real figures from the clipboard history, the screenshot folder, the file index and the
/// launcher's usage counts (nothing is collected just for this page). <see cref="ClipsPerDay"/> holds the clipboard
/// entries of the last 7 days, oldest first, so the last one is today.
/// </summary>
public sealed record HomeStats(
    int[] ClipsPerDay,
    IReadOnlyList<ClipItem> RecentClips,
    int ScreenshotsThisWeek,
    int IndexedFiles,
    IReadOnlyList<(string Name, int Count)> TopApps)
{
    public static readonly HomeStats Empty = new(new int[7], [], 0, 0, []);
}
