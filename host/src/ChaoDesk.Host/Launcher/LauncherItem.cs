using System.ComponentModel;
using System.Windows.Media;
using ChaoDesk.Host.Localization;

namespace ChaoDesk.Host.Launcher;

public enum LauncherItemKind { App, Command, Calculation, Web, File, Folder, Setup }

/// <summary>How the user chose an item: Enter, Ctrl+Enter, Ctrl+Shift+Enter, Ctrl+Shift+C.</summary>
public enum LaunchMode { Open, OpenFolder, RunAsAdmin, CopyPath }

/// <summary>A row in the launcher. File icons arrive after the row is shown, hence the change notification.</summary>
public sealed class LauncherItem : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private string _hint = "";

    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public LauncherItemKind Kind { get; init; }
    public int Score { get; set; }

    /// <summary>A file system path to reveal or copy (apps with a known executable, files, folders).</summary>
    public string? Path { get; init; }

    public AppEntry? App { get; init; }

    /// <summary>Shown in the empty box because it was used recently (it can be removed from the history).</summary>
    public bool FromHistory { get; set; }
    public Action<LaunchMode>? Run { get; init; }

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
    }

    /// <summary>Right-hand text: the quick-pick key ("Alt+1") or the kind.</summary>
    public string Hint
    {
        get => _hint;
        set { _hint = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hint))); }
    }

    public string KindLabel => Kind switch
    {
        LauncherItemKind.App => Loc.T("App"),
        LauncherItemKind.Command => Loc.T("Command"),
        LauncherItemKind.Calculation => Loc.T("Copy"),
        LauncherItemKind.Web => Loc.T("Open"),
        LauncherItemKind.File => Loc.T("File"),
        LauncherItemKind.Folder => Loc.T("Folder"),
        _ => "",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A built-in action listed alongside apps (settings, clipboard, lock, …).</summary>
public sealed class LauncherCommand
{
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public required Action Run { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    /// <summary>Stays the same in every language (usage and pins are stored under it), so it uses the English title.</summary>
    public string Id => "cmd:" + EnglishTitle;

    /// <summary>
    /// The title in English, whichever language <see cref="Title"/> was given in (the app may pass it already
    /// translated): English matching keeps working in Chinese and the id does not change with the language.
    /// </summary>
    public string EnglishTitle => EnglishOf(Title);

    /// <summary>The title in the interface language.</summary>
    public string ShownTitle => Loc.T(EnglishTitle);

    private static Dictionary<string, string>? _englishByChinese;

    private static string EnglishOf(string title)
    {
        _englishByChinese ??= Loc.Chinese.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);
        return _englishByChinese.TryGetValue(title, out var english) ? english : title;
    }

    private SearchKey[]? _keys;
    private string? _keysLanguage;

    /// <summary>The English title, the shown (Chinese, also by pinyin) title and the keywords all find the command.</summary>

    public SearchKey[] Keys
    {
        get
        {
            if (_keys == null || _keysLanguage != Loc.Language)
            {
                _keysLanguage = Loc.Language;
                var keys = new List<SearchKey> { new(EnglishTitle) };
                if (ShownTitle != EnglishTitle) keys.Add(new SearchKey(ShownTitle));
                keys.AddRange(Keywords.Select(k => new SearchKey(k)));
                _keys = keys.ToArray();
            }
            return _keys;
        }
    }
}
