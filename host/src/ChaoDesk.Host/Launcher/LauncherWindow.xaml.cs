using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ChaoDesk.Host.Files;
using ChaoDesk.Host.Localization;
using Serilog;

namespace ChaoDesk.Host.Launcher;

/// <summary>What the launcher needs from the app. Built by <see cref="AppHost"/>.</summary>
public sealed class LauncherContext
{
    public required AppCatalog Catalog { get; init; }
    public required FileIndexService Files { get; init; }
    public required LaunchStats Stats { get; init; }
    public required Action StatsChanged { get; init; }
    public required Func<IReadOnlyList<LauncherCommand>> Commands { get; init; }
    public required Func<bool> FileSearchEnabled { get; init; }
    public required Func<Task> SetUpFileSearch { get; init; }
    public required ImageSource? AppIcon { get; init; }
    public required Action<string> Preview { get; init; }

    /// <summary>The details column is shown at start; the window reports when the user toggles it.</summary>
    public required bool ShowDetails { get; init; }
    public required Action<bool> DetailsToggled { get; init; }
}

/// <summary>
/// The Alt+Space launcher: one search box over apps, Chao Desk commands, a calculator, web addresses and (with
/// file search set up) every file on the NTFS drives. Created once and hidden between uses so it opens instantly.
/// </summary>
public partial class LauncherWindow : Window
{
    private enum Filter { All, Apps, Files }
    private enum Sort { Relevance, Name, Modified, Size }

    /// <summary>Sorting by date or size looks at this many of the best matches (each needs a disk lookup).</summary>
    private const int SortPool = 1000;

    private const int AppsInAll = 8;
    private const int FilesInAll = 30;
    private const int AppsOnly = 60;
    private const int FilesOnly = 100;

    private readonly LauncherContext _ctx;
    private readonly ObservableCollection<LauncherItem> _items = new();
    private readonly DispatcherTimer _fileDebounce;
    private readonly ConcurrentDictionary<string, ImageSource?> _extIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock[] _chips;
    private CancellationTokenSource? _fileSearch;
    private Filter _filter = Filter.All;
    private Sort _sort = Sort.Relevance;
    private TextBlock? _sortChip;
    private string _fileStatus = "";
    private List<LauncherItem> _weakApps = new(); // shown after the files
    private Preview.PreviewPane? _preview;
    private readonly DispatcherTimer _previewDebounce;
    private LauncherItem? _detailsFor;
    private const double DetailsWidth = 440, FullWidth = 1080;
    private readonly Border _detailsButton;
    private readonly DispatcherTimer _leaveWatch;
    private bool _detailsOn;

    public LauncherWindow(LauncherContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        Results.ItemsSource = _items;
        _chips = new[] { Loc.T("All"), Loc.T("Apps"), Loc.T("Files") }.Select(MakeChip).ToArray();
        foreach (var chip in _chips) FilterPanel.Children.Add(chip);
        _sortChip = new TextBlock { Style = (Style)FindResource("FilterChip"), ToolTip = Loc.T("Sort files (Ctrl+S)"), Margin = new Thickness(10, 0, 2, 0) };
        _sortChip.MouseLeftButtonUp += (_, _) => { CycleSort(); QueryBox.Focus(); };
        FilterPanel.Children.Insert(0, _sortChip);
        UpdateChips();
        _detailsButton = IconButton.Create("\uE89F", "", () => { SetDetails(!_detailsOn, keepCentre: true); _ctx.DetailsToggled(_detailsOn); QueryBox.Focus(); });
        _detailsButton.Margin = new Thickness(14, 0, 0, 0);
        FilterPanel.Children.Add(_detailsButton);
        SetDetails(ctx.ShowDetails, keepCentre: false);

        // Files load their preview a moment after the selection settles (arrowing through results stays smooth).
        _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
        _previewDebounce.Tick += (_, _) => { _previewDebounce.Stop(); ShowDetails(); };
        Results.ContextMenu = new ContextMenu(); // filled when it opens, for the row under the mouse
        Results.ContextMenuOpening += OnContextMenuOpening;
        _fileDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _fileDebounce.Tick += (_, _) => { _fileDebounce.Stop(); StartFileSearch(); };

        SourceInitialized += (_, _) => PopupPlacement.RoundCorners(this);
        PopupPlacement.IgnoreAltMenu(this);
        _leaveWatch = PopupFocus.LeaveWatch(this, Hide);
        Deactivated += (_, _) =>
        {
            if (Results.ContextMenu?.IsOpen == true) return;
            if (PopupFocus.StillInside(this)) { _leaveWatch.Start(); return; } // into an Office preview: wait until really gone
            Hide();
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { _preview?.Release(); _detailsFor = null; } };
        PreviewKeyDown += OnKey;
        _ctx.Files.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        _ctx.Catalog.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });
    }

    public void Toggle()
    {
        if (IsVisible) { Hide(); return; }
        QueryBox.Text = "";
        _filter = Filter.All;
        UpdateChips();
        Refresh();
        PopupPlacement.PlaceOnActiveMonitor(this, 0.2);
        PopupPlacement.ShowActivated(this);
        Motion.Open(Root);
        QueryBox.Focus();
        _ctx.Catalog.RefreshIfOlderThan(TimeSpan.FromMinutes(2));
    }

    // ---- Building results -----------------------------------------------------------------------------

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    /// <summary>Rebuild the instant part (apps, commands, calculator, URL) now; files follow asynchronously.</summary>
    private void Refresh()
    {
        string query = QueryBox.Text;
        _fileSearch?.Cancel();
        _fileDebounce.Stop();
        _items.Clear();
        _weakApps = new();
        _fileStatus = "";

        string q = LauncherSearch.Normalize(query);
        if (q.Length > 0 && _filter != Filter.Files)
        {
            if (Calculator.TryEvaluate(q) is { } value)
            {
                string text = Calculator.Format(value);
                _items.Add(new LauncherItem
                {
                    Id = "calc", Title = "= " + text, Subtitle = Loc.T("Press Enter to copy the result"), Kind = LauncherItemKind.Calculation,
                    Run = _ => CopyText(text), Hint = Loc.T("Copy"),
                });
            }
            if (LauncherSearch.AsUrl(query) is { } url)
            {
                _items.Add(new LauncherItem
                {
                    Id = "url", Title = url, Subtitle = Loc.T("Open in your browser"), Kind = LauncherItemKind.Web, Hint = Loc.T("Open"),
                    Run = _ => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }),
                });
            }
        }

        if (_filter != Filter.Files)
        {
            var commands = _ctx.Commands();
            if (q.Length == 0)
            {
                // Empty box: pinned items, then the apps and commands used most.
                var byId = _ctx.Catalog.Apps.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
                foreach (var pin in _ctx.Stats.Pins)
                    if (PinItem(pin, byId, commands) is { } pinned) _items.Add(pinned);
                foreach (string id in _ctx.Stats.Frequent(AppsInAll + _ctx.Stats.Pins.Count).Where(id => !_ctx.Stats.IsPinned(id)).Take(AppsInAll))
                {
                    var used = byId.TryGetValue(id, out var app) ? AppItem(app)
                        : commands.FirstOrDefault(c => c.Id == id) is { } cmd ? CommandItem(cmd) : null;
                    if (used == null) continue;
                    used.FromHistory = true;
                    used.Hint = Loc.T("Recent");
                    _items.Add(used);
                }
            }
            else
            {
                // Pinned files and folders whose name matches come first (pinned apps get a boost in the ranking).
                foreach (var pin in _ctx.Stats.Pins.Where(p => p.Kind is LauncherItemKind.File or LauncherItemKind.Folder && p.Path != null))
                    if (new SearchKey(pin.Title).Match(q) > 0 && (File.Exists(pin.Path) || Directory.Exists(pin.Path)))
                    {
                        var pinned = FileItem(new FileHit(pin.Path!, pin.Title, pin.Kind == LauncherItemKind.Folder));
                        pinned.Hint = Loc.T("Pinned");
                        _items.Add(pinned);
                    }
                int take = _filter == Filter.Apps ? AppsOnly : AppsInAll;
                foreach (var r in LauncherSearch.Rank(_ctx.Catalog.Apps, commands, query, _ctx.Stats, DateTime.Now).Take(take))
                {
                    var item = r.App != null ? AppItem(r.App) : CommandItem(r.Command!);
                    if (r.IsStrong || _filter == Filter.Apps) _items.Add(item);
                    else _weakApps.Add(item);
                }
            }
        }

        if (_filter != Filter.Apps && WantsFileSearch(q))
        {
            if (_ctx.Files.CanSetUp && _ctx.FileSearchEnabled()) { AddWeakApps(); _items.Add(SetupItem()); }
            else if (_ctx.FileSearchEnabled() && _ctx.Files.CanSearch) _fileDebounce.Start();
            else AddWeakApps();
        }
        else AddWeakApps();

        AfterItemsChanged();
    }

    private static bool WantsFileSearch(string q) => q.Length >= 2 || (q.Length == 1 && Pinyin.IsHan(q[0]));

    private void StartFileSearch()
    {
        if (!_ctx.FileSearchEnabled() || !_ctx.Files.CanSearch) { AddWeakApps(); AfterItemsChanged(); return; }
        string query = QueryBox.Text;
        var sort = _filter == Filter.Files ? _sort : Sort.Relevance;
        int show = _filter == Filter.Files ? FilesOnly : FilesInAll;
        int limit = sort == Sort.Relevance ? show : SortPool;
        var shown = _items.Where(i => i.Path != null).Select(i => i.Path!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cts = new CancellationTokenSource();
        _fileSearch = cts;
        var started = Stopwatch.StartNew();
        Task.Run(() =>
        {
            var result = _ctx.Files.Search(query, limit, cts.Token);
            // Size and date come from the disk (the index keeps names only); look up just the hits.
            var stats = result.Hits.Select(h => (Hit: h, Info: Stat(h))).ToList();
            stats = sort switch
            {
                Sort.Name => stats.OrderBy(x => x.Hit.Name, Comparer<string>.Create((a, b) => Desktop.NativeMethods.StrCmpLogicalW(a, b))).ToList(),
                Sort.Modified => stats.OrderByDescending(x => x.Info.Modified).ToList(),
                Sort.Size => stats.OrderByDescending(x => x.Info.Size).ToList(),
                _ => stats,
            };
            return (result.Total, Stats: stats.Take(show).ToList());
        }, cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || t.IsFaulted || t.IsCanceled || QueryBox.Text != query) return;
            var (total, stats) = t.Result;
            foreach (var (hit, info) in stats)
            {
                if (shown.Contains(hit.Path)) continue; // already listed as a pin
                var item = FileItem(hit);
                item.Hint = Describe(hit, info);
                _items.Add(item);
            }
            AddWeakApps();
            string by = sort switch { Sort.Name => Loc.T("by name"), Sort.Modified => Loc.T("by date modified"), Sort.Size => Loc.T("by size"), _ => "" };
            if (by.Length > 0 && total > SortPool) by = Loc.F("{0} among the best {1:N0}", by, SortPool);
            string sorted = by.Length == 0 ? "" : " · " + by;
            _fileStatus = total == 0 ? Loc.T("No files match")
                : total == 1 ? Loc.F("{0:N0} file matches{1} · {2} ms", total, sorted, started.ElapsedMilliseconds)
                : Loc.F("{0:N0} files match{1} · {2} ms", total, sorted, started.ElapsedMilliseconds);
            AfterItemsChanged();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private readonly record struct FileStat(long Size, DateTime Modified);

    private static FileStat Stat(FileHit hit)
    {
        try
        {
            if (hit.IsDirectory) return new FileStat(-1, Directory.GetLastWriteTime(hit.Path));
            var fi = new FileInfo(hit.Path);
            return fi.Exists ? new FileStat(fi.Length, fi.LastWriteTime) : new FileStat(-1, DateTime.MinValue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return new FileStat(-1, DateTime.MinValue); }
    }

    /// <summary>"12.3 MB · 10-01" (folders: the date only).</summary>
    private static string Describe(FileHit hit, FileStat info)
    {
        if (info.Modified == DateTime.MinValue) return hit.IsDirectory ? Loc.T("Folder") : Loc.T("File");
        string date = info.Modified.ToString(info.Modified.Year == DateTime.Now.Year ? "MM-dd HH:mm" : "yyyy-MM-dd");
        return info.Size >= 0 ? $"{Preview.QuickLookWindow.FormatSize(info.Size)} · {date}" : date;
    }

    private void CycleSort()
    {
        _sort = (Sort)(((int)_sort + 1) % 4);
        if (_filter != Filter.Files) _filter = Filter.Files;
        UpdateChips();
        Refresh();
    }

    /// <summary>Ctrl+P: pin or unpin the selected app, command, file or folder.</summary>
    private void TogglePin()
    {
        if (Results.SelectedItem is not LauncherItem { Kind: LauncherItemKind.App or LauncherItemKind.Command or LauncherItemKind.File or LauncherItemKind.Folder } item) return;
        bool pinned = _ctx.Stats.TogglePin(item);
        item.Hint = pinned ? Loc.T("Pinned") : item.KindLabel;
        _ctx.StatsChanged();
        StatusText.Text = pinned ? Loc.F("Pinned {0}: it shows first when the box is empty.", item.Title) : Loc.F("Unpinned {0}.", item.Title);
    }

    /// <summary>A pinned item as a row (null when its app is gone or its file was deleted).</summary>
    private LauncherItem? PinItem(LaunchStats.Pin pin, Dictionary<string, AppEntry> apps, IReadOnlyList<LauncherCommand> commands)
    {
        LauncherItem? item = pin.Kind switch
        {
            LauncherItemKind.App => apps.TryGetValue(pin.Id, out var app) ? AppItem(app) : null,
            LauncherItemKind.Command => commands.FirstOrDefault(c => c.Id == pin.Id) is { } cmd ? CommandItem(cmd) : null,
            LauncherItemKind.File when pin.Path != null && File.Exists(pin.Path) => FileItem(new FileHit(pin.Path, pin.Title, false)),
            LauncherItemKind.Folder when pin.Path != null && Directory.Exists(pin.Path) => FileItem(new FileHit(pin.Path, pin.Title, true)),
            _ => null,
        };
        if (item != null) item.Hint = Loc.T("Pinned");
        return item;
    }

    private void AddWeakApps()
    {
        foreach (var item in _weakApps) _items.Add(item);
        _weakApps = new();
    }

    private void AfterItemsChanged()
    {
        bool show = _items.Count > 0;
        if (show && ResultsHost.Visibility != Visibility.Visible)
        {
            ResultsHost.Visibility = Visibility.Visible;
            Motion.Expand(ResultsHost, 480); // grows open instead of popping (the window follows its height)
        }
        else if (!show && ResultsHost.Visibility == Visibility.Visible)
        {
            ResultsHost.BeginAnimation(HeightProperty, null);
            ResultsHost.Visibility = Visibility.Collapsed;
        }
        // Never preselect the setup row: Enter right after typing must not start an administrator prompt.
        if (Results.SelectedIndex < 0) Results.SelectedIndex = _items.ToList().FindIndex(i => i.Kind != LauncherItemKind.Setup);
        UpdateStatus();
        UpdateKeys();
        ClearHistoryLink.Visibility = _items.Any(i => i.FromHistory) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The ✕ on a recent row, its menu item and Delete (with an empty box): forget that one item.</summary>
    private void ForgetItem(LauncherItem item)
    {
        if (!item.FromHistory) return;
        int index = Results.SelectedIndex;
        _ctx.Stats.Forget(item.Id);
        _ctx.StatsChanged();
        Refresh();
        if (_items.Count > 0) Results.SelectedIndex = Math.Clamp(index, 0, _items.Count - 1);
        StatusText.Text = Loc.F("Removed {0} from the history.", item.Title);
    }

    private void ClearHistory()
    {
        _ctx.Stats.ClearHistory();
        _ctx.StatsChanged();
        Refresh();
        StatusText.Text = Loc.T("History cleared. Pinned items stay.");
        QueryBox.Focus();
    }

    private void OnClearHistoryClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ClearHistory();
    }

    private LauncherItem AppItem(AppEntry app) => new()
    {
        Id = app.Id,
        Title = app.Name,
        Subtitle = app.Path ?? Loc.T("Microsoft Store app"),
        Kind = LauncherItemKind.App,
        Path = app.Path,
        App = app,
        Icon = app.Icon,
        Hint = _ctx.Stats.IsPinned(app.Id) ? Loc.T("Pinned") : Loc.T("App"),
    };

    private LauncherItem CommandItem(LauncherCommand cmd) => new()
    {
        Id = cmd.Id,
        Title = cmd.ShownTitle,
        Subtitle = Loc.T(cmd.Subtitle),
        Kind = LauncherItemKind.Command,
        Run = _ => cmd.Run(),
        Icon = _ctx.AppIcon,
        Hint = "Chao Desk",
    };

    private LauncherItem SetupItem() => new()
    {
        Id = "setup-files",
        Title = _ctx.Files.State == FileIndexState.NeedsRepair ? Loc.T("Set up file search again") : Loc.T("Set up file search"),
        Subtitle = Loc.T("Search every file instantly, like Everything. Windows asks once for administrator permission."),
        Kind = LauncherItemKind.Setup,
        Icon = ExtensionIcon("\\"),
        Hint = "Enter",
        Run = mode => _ctx.SetUpFileSearch(),
    };

    private LauncherItem FileItem(FileHit hit)
    {
        var item = new LauncherItem
        {
            Id = "file:" + hit.Path,
            Title = hit.Name,
            Subtitle = Path.GetDirectoryName(hit.Path) ?? hit.Path,
            Kind = hit.IsDirectory ? LauncherItemKind.Folder : LauncherItemKind.File,
            Path = hit.Path,
            Hint = hit.IsDirectory ? Loc.T("Folder") : Loc.T("File"),
        };
        string ext = hit.IsDirectory ? "\\" : Path.GetExtension(hit.Name);
        if (ext is ".exe" or ".lnk" or ".ico" or ".url" or ".appref-ms")
        {
            // These carry their own icon; read it from the file on the shell thread.
            _ctx.Catalog.Post(() =>
            {
                var icon = ShellIcons.ForPath(hit.Path);
                Dispatcher.BeginInvoke(() => item.Icon = icon ?? ExtensionIcon(ext));
            });
        }
        else item.Icon = ExtensionIcon(ext);
        return item;
    }

    private ImageSource? ExtensionIcon(string ext) => _extIcons.GetOrAdd(ext.Length == 0 ? "." : ext, e => ShellIcons.ForExtension(e));

    // ---- Status and key hints ---------------------------------------------------------------------------

    private void UpdateStatus()
    {
        if (!_ctx.FileSearchEnabled()) StatusText.Text = _filter == Filter.Files ? Loc.T("File search is turned off in Settings.") : "";
        else if (_fileStatus.Length > 0 && _ctx.Files.State == FileIndexState.Ready) StatusText.Text = _fileStatus;
        else StatusText.Text = _ctx.Files.StatusText;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Apps and commands show their details at once; files after a short pause (previews can be heavy).
        _previewDebounce.Stop();
        if (Results.SelectedItem is LauncherItem { Kind: LauncherItemKind.File or LauncherItemKind.Folder }) _previewDebounce.Start();
        else ShowDetails();
        UpdateKeys();
        if (Results.SelectedItem != null) Results.ScrollIntoView(Results.SelectedItem);
    }

    private void UpdateKeys()
    {
        KeysText.Text = (Results.SelectedItem as LauncherItem)?.Kind switch
        {
            LauncherItemKind.App => Loc.T("Enter or double-click open · right-click more · Ctrl+P pin"),
            LauncherItemKind.File or LauncherItemKind.Folder => Loc.T("Double-click open · Shift+Enter Quick Look · right-click more · Ctrl+S sort"),
            LauncherItemKind.Calculation => Loc.T("Enter copy"),
            null => Loc.T("Tab: All / Apps / Files"),
            _ => "Enter",
        };
    }

    private TextBlock MakeChip(string text, int index)
    {
        var chip = new TextBlock { Text = text, Style = (Style)FindResource("FilterChip") };
        chip.MouseLeftButtonUp += (_, _) => { _filter = (Filter)index; UpdateChips(); Refresh(); QueryBox.Focus(); };
        return chip;
    }

    private void UpdateChips()
    {
        if (_sortChip != null)
        {
            _sortChip.Visibility = _filter == Filter.Files ? Visibility.Visible : Visibility.Collapsed;
            _sortChip.Text = _sort switch { Sort.Name => Loc.T("↓ Name"), Sort.Modified => Loc.T("↓ Date"), Sort.Size => Loc.T("↓ Size"), _ => Loc.T("↓ Relevance") };
        }
        for (int i = 0; i < _chips.Length; i++)
        {
            bool on = (int)_filter == i;
            FilterTab.Set(_chips[i], on);
        }
    }

    // ---- Keyboard and mouse -----------------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape:
                Hide();
                break;
            case Key.Down: Move(1); break;
            case Key.Up: Move(-1); break;
            case Key.PageDown: Move(8); break;
            case Key.PageUp: Move(-8); break;
            case Key.Tab:
                _filter = (Filter)(((int)_filter + (mods.HasFlag(ModifierKeys.Shift) ? 2 : 1)) % 3);
                UpdateChips();
                Refresh();
                break;
            case Key.Enter when mods == ModifierKeys.Shift:
                if (Results.SelectedItem is LauncherItem { Kind: LauncherItemKind.File or LauncherItemKind.Folder, Path: { } quickLook }) { Hide(); _ctx.Preview(quickLook); }
                break;
            case Key.Enter:
                var mode = mods.HasFlag(ModifierKeys.Control) ? (mods.HasFlag(ModifierKeys.Shift) ? LaunchMode.RunAsAdmin : LaunchMode.OpenFolder) : LaunchMode.Open;
                RunSelected(mode);
                break;
            case Key.Delete when QueryBox.Text.Length == 0 && Results.SelectedItem is LauncherItem { FromHistory: true } recent:
                ForgetItem(recent);
                break;
            case Key.P when mods == ModifierKeys.Control:
                TogglePin();
                break;
            case Key.S when mods == ModifierKeys.Control:
                CycleSort();
                break;
            case Key.C when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                RunSelected(LaunchMode.CopyPath);
                break;
            case >= Key.D1 and <= Key.D9 when mods == ModifierKeys.Alt:
                int index = key - Key.D1;
                if (index < _items.Count) { Results.SelectedIndex = index; RunSelected(LaunchMode.Open); }
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Move(int delta)
    {
        if (_items.Count == 0) return;
        int i = Results.SelectedIndex + delta;
        if (Math.Abs(delta) == 1) i = (i + _items.Count) % _items.Count; // wrap with the arrows
        Results.SelectedIndex = Math.Clamp(i, 0, _items.Count - 1);
    }

    private LauncherItem? ItemAt(MouseButtonEventArgs e) =>
        (e.OriginalSource as DependencyObject) is { } d && ItemsControl.ContainerFromElement(Results, d) is ListBoxItem { DataContext: LauncherItem item } ? item : null;

    /// <summary>A click selects (and previews a file or folder); a double-click opens. Rows are not focusable, so we select ourselves.</summary>
    private void OnResultMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e) is not { } item) return;
        e.Handled = true;
        if (e.OriginalSource is FrameworkElement { Tag: "forget" }) { ForgetItem(item); QueryBox.Focus(); return; }
        Results.SelectedItem = item;
        QueryBox.Focus();
        if (e.ClickCount >= 2) RunSelected(LaunchMode.Open); // a single click only selects (and shows details)
    }

    private void OnResultRightDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e) is { } item) Results.SelectedItem = item;
    }

    // ---- Details column -------------------------------------------------------------------------------

    /// <summary>Shows or hides the details column (the window narrows around its centre).</summary>
    private void SetDetails(bool on, bool keepCentre)
    {
        _detailsOn = on;
        PreviewHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        double width = on ? FullWidth : FullWidth - DetailsWidth;
        if (keepCentre && !double.IsNaN(Left)) Left += (Width - width) / 2;
        Width = width;
        IconButton.Set(_detailsButton, on, null, on ? Loc.T("Hide the details and preview") : Loc.T("Show the details and preview"));
        if (on) { _detailsFor = null; ShowDetails(); }
        else _preview?.Release();
    }

    /// <summary>Fills the details column for the selected result (files: a preview; others: what they are).</summary>
    private void ShowDetails()
    {
        if (!_detailsOn) return;
        var item = Results.SelectedItem as LauncherItem;
        // While typing the list is cleared and refilled on every key: keep the old details until a new row is selected.
        if (item == null || item == _detailsFor) return;
        _detailsFor = item;
        if (item is { Kind: LauncherItemKind.File or LauncherItemKind.Folder, Path: { } path })
        {
            _preview ??= new Preview.PreviewPane { Inline = true };
            _preview.DetailsChanged -= OnPreviewDetails;
            _preview.DetailsChanged += OnPreviewDetails;
            bool changed = _preview.CurrentPath != path || PreviewSlot.Content != _preview;
            if (_preview.CurrentPath != path) _preview.Load(path);
            PreviewSlot.Content = _preview;
            if (changed) Motion.SlideIn(PreviewSlot);
            PreviewTitle.Text = item.Title;
            PreviewDetails.Text = _preview.Details;
            return;
        }
        _preview?.Release(); // stop whatever the last file preview was doing
        PreviewTitle.Text = item?.Title ?? "";
        PreviewDetails.Text = item == null ? "" : item.Kind switch
        {
            LauncherItemKind.App => item.App?.Path == null ? Loc.T("Microsoft Store app") : Loc.T("Desktop app"),
            LauncherItemKind.Command => Loc.T("Chao Desk command"),
            LauncherItemKind.Calculation => Loc.T("Calculation"),
            LauncherItemKind.Web => Loc.T("Web address"),
            _ => "",
        };
        PreviewSlot.Content = item == null ? null : FactsView(item);
        Motion.SlideIn(PreviewSlot);
    }

    private void OnPreviewDetails() => PreviewDetails.Text = _preview?.Details ?? "";

    /// <summary>A big icon and a few facts: for apps the program, version and how often you use it.</summary>
    private FrameworkElement FactsView(LauncherItem item)
    {
        var stack = new StackPanel { Margin = new Thickness(28, 32, 28, 20) };
        if (item.Kind == LauncherItemKind.Calculation)
        {
            var big = new TextBlock { Text = item.Title, FontSize = 34, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            stack.Children.Add(big);
            stack.Children.Add(Fact(Loc.T("Enter or double-click copies the result.")));
            return stack;
        }
        stack.Children.Add(new Image { Source = item.Icon, Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) });
        stack.Children.Add(new TextBlock { Text = item.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (item.Subtitle.Length > 0 && item.Kind != LauncherItemKind.App) stack.Children.Add(Fact(item.Subtitle));
        if (item.Kind == LauncherItemKind.App)
        {
            if (item.App?.Path is { } exe && File.Exists(exe))
            {
                try
                {
                    var v = FileVersionInfo.GetVersionInfo(exe);
                    if (!string.IsNullOrWhiteSpace(v.FileDescription) && v.FileDescription != item.Title) stack.Children.Add(Fact(v.FileDescription!));
                    string company = string.IsNullOrWhiteSpace(v.CompanyName) ? "" : v.CompanyName!.Trim();
                    string version = string.IsNullOrWhiteSpace(v.ProductVersion) ? "" : Loc.F("version {0}", v.ProductVersion!.Trim());
                    string line = string.Join(" · ", new[] { company, version }.Where(x => x.Length > 0));
                    if (line.Length > 0) stack.Children.Add(Fact(line));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                stack.Children.Add(Fact(exe, mono: true));
            }
            else stack.Children.Add(Fact(Loc.T("Installed from the Microsoft Store")));
        }
        if (_ctx.Stats.Items.TryGetValue(item.Id, out var used) && used.Count > 0)
            stack.Children.Add(Fact(used.Count == 1
                ? Loc.F("Opened {0} time from here · last {1:yyyy-MM-dd HH:mm}", used.Count, used.Last)
                : Loc.F("Opened {0} times from here · last {1:yyyy-MM-dd HH:mm}", used.Count, used.Last)));
        if (_ctx.Stats.IsPinned(item.Id)) stack.Children.Add(Fact(Loc.T("Pinned — shown first when the search box is empty")));
        stack.Children.Add(Fact(item.Kind switch
        {
            LauncherItemKind.App => Loc.T("Enter or double-click opens · right-click for more"),
            LauncherItemKind.Web => Loc.T("Enter or double-click opens it in your browser"),
            LauncherItemKind.Setup => Loc.T("Enter or double-click starts the setup (Windows asks once for permission)"),
            _ => Loc.T("Enter or double-click runs it"),
        }, top: 18));
        return stack;
    }

    private static TextBlock Fact(string text, bool mono = false, double top = 6)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        if (mono) t.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }

    // ---- Right-click menu --------------------------------------------------------------------------------

    /// <summary>Everything the keys do (and a little more), for the mouse.</summary>
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = Results.ContextMenu!;
        menu.Items.Clear();
        // The menu is for the row under the mouse (the menu key uses the selection); empty space gets no row menu.
        var item = e.CursorLeft < 0 ? Results.SelectedItem as LauncherItem
            : (e.OriginalSource as DependencyObject) is { } d && ItemsControl.ContainerFromElement(Results, d) is ListBoxItem { DataContext: LauncherItem hit } ? hit : null;
        if (item == null)
        {
            if (_items.Any(i => i.FromHistory))
            {
                var clear = new MenuItem { Header = Loc.T("Clear history") };
                clear.Click += (_, _) => ClearHistory();
                menu.Items.Add(clear);
            }
            else e.Handled = true;
            return;
        }
        void Add(string header, string gesture, Action action, bool bold = false)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        void Separator() { if (menu.Items.Count > 0 && menu.Items[^1] is not System.Windows.Controls.Separator) menu.Items.Add(new Separator()); }
        string? path = item.Path;
        bool isFile = item.Kind == LauncherItemKind.File, isFolder = item.Kind == LauncherItemKind.Folder;

        Add(item.Kind switch { LauncherItemKind.Command => Loc.T("Run"), LauncherItemKind.Calculation => Loc.T("Copy result"), _ => Loc.T("Open") }, Loc.T("Enter / double-click"), () => RunSelected(LaunchMode.Open), bold: true);
        if (isFile || isFolder) Add(Loc.T("Quick Look"), "Shift+Enter", () => { Hide(); _ctx.Preview(path!); });
        if (isFile) Add(Loc.T("Open with…"), "", () => OpenWith(path!));
        if ((item.Kind == LauncherItemKind.App || isFile && path!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            Add(Loc.T("Run as administrator"), "Ctrl+Shift+Enter", () => RunSelected(LaunchMode.RunAsAdmin));
        Separator();
        if (path != null)
        {
            Add(isFolder ? Loc.T("Open containing folder") : Loc.T("Show in folder"), "Ctrl+Enter", () => RunSelected(LaunchMode.OpenFolder));
            Add(Loc.T("Copy path"), "Ctrl+Shift+C", () => RunSelected(LaunchMode.CopyPath));
            if (isFile || isFolder) Add(Loc.T("Copy file"), "", () => CopyFile(path));
        }
        if (item.Kind is LauncherItemKind.App or LauncherItemKind.Command or LauncherItemKind.File or LauncherItemKind.Folder)
        {
            Separator();
            Add(_ctx.Stats.IsPinned(item.Id) ? Loc.T("Unpin") : Loc.T("Pin to the top"), "Ctrl+P", TogglePin);
        }
        if (item.FromHistory)
        {
            Add(Loc.T("Remove from history"), "Del", () => ForgetItem(item));
            Add(Loc.T("Clear history"), "", ClearHistory);
        }
        if (path != null && (File.Exists(path) || Directory.Exists(path)))
        {
            Separator();
            Add(Loc.T("Properties"), "", () => Desktop.NativeMethods.SHObjectProperties(new System.Windows.Interop.WindowInteropHelper(this).Handle, Desktop.NativeMethods.SHOP_FILEPATH, path, null));
        }
    }

    private void OpenWith(string path)
    {
        try { Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}") { UseShellExecute = false }); Hide(); }
        catch (Exception ex) { Log.Warning(ex, "Open with failed for {Path}", path); }
    }

    private static void CopyFile(string path)
    {
        try { Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { path }); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void RunSelected(LaunchMode mode)
    {
        if (Results.SelectedItem is not LauncherItem item) return;
        string query = LauncherSearch.Normalize(QueryBox.Text);
        try
        {
            switch (mode)
            {
                case LaunchMode.CopyPath when item.Path != null:
                    CopyText(item.Path);
                    break;
                case LaunchMode.OpenFolder when item.Path != null:
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
                    break;
                case LaunchMode.RunAsAdmin when item.Kind is LauncherItemKind.File && item.Path != null:
                    Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true, Verb = "runas" });
                    break;
                default:
                    if (item.App != null) AppCatalog.Launch(item.App, mode == LaunchMode.RunAsAdmin);
                    else if (item.Run != null) item.Run(mode);
                    else if (item.Path != null) Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                    break;
            }
            if (item.Kind is LauncherItemKind.App or LauncherItemKind.Command)
            {
                _ctx.Stats.Record(item.Id, query, DateTime.Now);
                _ctx.StatsChanged();
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { /* the user cancelled UAC */ }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Item}", item.Title);
            StatusText.Text = Loc.F("Could not open {0}: {1}", item.Title, ex.Message);

            return;
        }
        Hide();
    }

    private static void CopyText(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetDataObject(text, copy: true); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(30); }
        }
    }
}
