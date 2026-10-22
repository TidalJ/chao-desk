using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChaoDesk.Host.Input;
using ChaoDesk.Host.Launcher;
using ChaoDesk.Host.Localization;
using ChaoDesk.Host.Translate;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Clips;

/// <summary>A row in the clipboard list.</summary>
public sealed class ClipRow : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;

    public required ClipItem Item { get; init; }

    /// <summary>Whether the entry was pinned when the row was made (rows are reused only while that still holds).</summary>
    public bool PinnedWhenMade { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public string Glyph => Item.Kind switch { ClipKind.Image => "", ClipKind.Files => "", _ => "" };
    public string PinGlyph => Item.Pinned ? "" : "";

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The clipboard history popup (Shift+` by default). Enter pastes the selected entry into the window the user came
/// from: we put it on the clipboard, give that window the focus back and send Ctrl+V. Pinned (the pin button), it
/// stays on screen: paste one entry after another into whichever window was used last, or drag entries out.
/// </summary>
public partial class ClipboardWindow : Window
{
    /// <summary>The filter tabs, in <see cref="ClipFilter"/> order (in the interface language when read).</summary>
    private static string[] FilterNames => [Loc.T("All"), Loc.T("Pinned"), Loc.T("Text"), Loc.T("Images"), Loc.T("Files")];

    private readonly ClipboardHistory _history;
    private readonly ClipboardMonitor _monitor;
    private readonly TextBlock[] _chips;
    private readonly Dictionary<string, ImageSource?> _thumbs = new();
    private const double ListWidth = 380, FullWidth = 800; // the list column (as in the XAML), and with the preview

    private readonly Border _translateButton, _previewButton, _pinButton;
    private Preview.PreviewPane? _filePane;
    private string? _filePanePath, _pendingFile;
    private readonly System.Windows.Threading.DispatcherTimer _fileDebounce;
    private readonly System.Windows.Threading.DispatcherTimer _leaveWatch;
    private readonly System.Collections.ObjectModel.ObservableCollection<ClipRow> _rows = new();
    private ClipFilter _filter = ClipFilter.All;
    private IntPtr _previous;
    private bool _pinned;
    private Point _dragStart;
    private ClipRow? _dragRow;

    /// <summary>Reads the text in a picture entry (its menu, or Ctrl+T).</summary>
    public Action<BitmapSource>? Recognize { get; init; }

    /// <summary>Opens a file in Quick Look (Space on an empty search box, for pictures and copied files).</summary>
    public Action<string>? QuickLook { get; init; }

    /// <summary>Translates a text entry (Ctrl+T or its menu) with the user's service and target.</summary>
    public Func<string, CancellationToken, Task<Translation>>? Translate { get; init; }

    // Shown in the preview under the entry's text; Result is null while the service is still working on it.
    private (string Id, Translation? Result)? _translation;
    private CancellationTokenSource? _translating;
    private readonly Dictionary<string, Translation> _translated = new(); // by entry: hiding and showing again is instant
    private int _previewToggles; // a closing preview ignores its end if it was reopened meanwhile
    private int _imageLoads;     // only the newest picture preview is shown

    private string _lastQuery = "";

    /// <summary>The preview column is shown (the button in the search bar toggles it; the app remembers it).</summary>
    public bool PreviewOn { get; private set; }
    public event Action<bool>? PreviewToggled;

    public ClipboardWindow(ClipboardHistory history, ClipboardMonitor monitor, bool showPreview)
    {
        _history = history;
        _monitor = monitor;
        InitializeComponent();
        // Office previews are slow to start: load a file's preview once the selection rests for a moment.
        // (Created first: SetPreview below already uses it.)
        _fileDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _fileDebounce.Tick += (_, _) => { _fileDebounce.Stop(); LoadPendingFile(); };
        _chips = FilterNames.Select(MakeChip).ToArray();
        foreach (var chip in _chips) FilterPanel.Children.Add(chip);
        UpdateChips();
        _previewButton = IconButton.Create("\uE89F", "", () => { SetPreview(!PreviewOn, animate: true); PreviewToggled?.Invoke(PreviewOn); QueryBox.Focus(); });
        _previewButton.Margin = new Thickness(14, 0, 0, 0);
        _pinButton = IconButton.Create("\uE718", "", () => { SetPinned(!_pinned); QueryBox.Focus(); });
        // Ctrl+T for the mouse: translate the selected text, or read the text in the selected picture.
        _translateButton = IconButton.Create("\uE8C1", "", () => { if (Results.SelectedItem is ClipRow r) TranslateOrRecognize(r.Item); QueryBox.Focus(); });
        _translateButton.Margin = new Thickness(14, 0, 0, 0);
        _previewButton.Margin = new Thickness(4, 0, 0, 0);
        ButtonPanel.Children.Add(_translateButton);
        ButtonPanel.Children.Add(_previewButton);
        ButtonPanel.Children.Add(_pinButton);
        IsVisibleChanged += (_, _) => { if (!IsVisible) ReleaseFilePreview(); };
        SetPreview(showPreview, animate: false);
        SetPinned(false);
        SourceInitialized += (_, _) => PopupPlacement.RoundCorners(this);
        PopupPlacement.IgnoreAltMenu(this);
        Results.ItemsSource = _rows;
        _leaveWatch = PopupFocus.LeaveWatch(this, HideUnlessPinned);
        Deactivated += (_, _) =>
        {
            if (Results.ContextMenu?.IsOpen == true || PreviewText.ContextMenu?.IsOpen == true) return;
            // Clicking into a Word or PowerPoint preview (another process) is not leaving.
            if (PopupFocus.StillInside(this)) { _leaveWatch.Start(); return; }
            if (!_pinned) { Hide(); return; }
            // Pinned: the window the user went to is where the next paste goes.
            _previous = GetForegroundWindow();
        };
        PreviewText.ContextMenu = PreviewMenu();
        Results.PreviewMouseMove += OnResultMouseMove;
        Results.PreviewMouseLeftButtonUp += (_, _) => _dragRow = null;
        PreviewKeyDown += OnKey;
        _history.Changed += () => { if (IsVisible) Refresh(keepSelection: true); };
        Results.ContextMenu = BuildContextMenu();
        // The menu is for the row under the mouse; empty space has none (the menu key uses the selection).
        Results.ContextMenuOpening += (_, e) =>
        {
            if (e.CursorLeft < 0) { if (Results.SelectedItem == null) e.Handled = true; return; }
            if ((e.OriginalSource as DependencyObject) is not { } d || ItemsControl.ContainerFromElement(Results, d) is not ListBoxItem) e.Handled = true;
        };
        Results.PreviewMouseRightButtonDown += (_, e) =>
        {
            if ((e.OriginalSource as DependencyObject) is { } d && ItemsControl.ContainerFromElement(Results, d) is ListBoxItem { DataContext: ClipRow row })
                Results.SelectedItem = row;
        };
    }

    /// <summary>The preview's own menu: copy what is selected (or all of it).</summary>
    private ContextMenu PreviewMenu()
    {
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = Loc.T("Copy selection"), InputGestureText = "Ctrl+C" };
        copy.Click += (_, _) => PreviewText.Copy();
        var all = new MenuItem { Header = Loc.T("Select all"), InputGestureText = "Ctrl+A" };
        all.Click += (_, _) => { PreviewText.Focus(); PreviewText.SelectAll(); };
        menu.Items.Add(copy);
        menu.Items.Add(all);
        menu.Opened += (_, _) => copy.IsEnabled = PreviewText.SelectionLength > 0;
        return menu;
    }

    /// <summary>Everything the keys do, for the mouse.</summary>
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string gesture, Action<ClipItem> action)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture };
            mi.Click += (_, _) => { if (Results.SelectedItem is ClipRow r) action(r.Item); };
            menu.Items.Add(mi);
            return mi;
        }
        Item(Loc.T("Paste"), "Enter", i => _ = PasteAsync(i, false));
        Item(Loc.T("Paste as plain text"), "Shift+Enter", i => _ = PasteAsync(i, true));
        Item(Loc.T("Copy"), "Ctrl+C", i => { if (Put(i, false)) { _history.Touch(i.Id); HideUnlessPinned(); } });
        menu.Items.Add(new Separator());
        var recognize = Item(Loc.T("Recognise text"), "Ctrl+T", i => RecognizeEntry(i));
        var translate = Item(Loc.T("Translate"), "Ctrl+T", i => _ = ToggleTranslationAsync(i));
        menu.Items.Add(new Separator());
        var pin = Item(Loc.T("Pin"), "Ctrl+P", i => _history.TogglePin(i.Id));
        Item(Loc.T("Delete"), "Del", i => _history.Remove(i.Id));
        menu.Opened += (_, _) =>
        {
            var item = (Results.SelectedItem as ClipRow)?.Item;
            pin.Header = item?.Pinned == true ? Loc.T("Unpin") : Loc.T("Pin");
            recognize.Visibility = item != null && Recognize != null && PictureOf(item) != null ? Visibility.Visible : Visibility.Collapsed;
            translate.Visibility = item != null && CanTranslate(item) ? Visibility.Visible : Visibility.Collapsed;
            translate.Header = item != null && _translation?.Id == item.Id ? Loc.T("Hide translation") : Loc.T("Translate");
        };
        // The menu takes focus; do not let that count as leaving the popup.
        menu.Closed += (_, _) => { if (IsVisible) { Activate(); QueryBox.Focus(); } };
        return menu;
    }

    public void Toggle()
    {
        if (IsVisible && (!_pinned || IsActive)) { Hide(); return; }
        if (IsVisible) { Activate(); QueryBox.Focus(); return; } // pinned behind another window's focus: bring it back
        _previous = GetForegroundWindow();
        _translating?.Cancel();
        _translation = null;
        QueryBox.Text = "";
        _lastQuery = "";
        _filter = ClipFilter.All;
        UpdateChips();
        Refresh(keepSelection: false);
        UpdateTranslateButton();
        if (!_pinned || double.IsNaN(Left)) PopupPlacement.PlaceOnActiveMonitor(this, 0.16); // a pinned window stays where it was put
        PopupPlacement.ShowActivated(this);
        Motion.Open(Root);
        QueryBox.Focus();
    }

    // ---- Pin and preview -------------------------------------------------------------------------------

    private void SetPinned(bool pinned)
    {
        _pinned = pinned;
        IconButton.Set(_pinButton, pinned, pinned ? "\uE77A" : "\uE718",
            pinned ? Loc.T("Pinned: stays open while you paste or drag entries into other windows. Click to unpin.")
                   : Loc.T("Pin: keep the clipboard open on screen"));
        if (IsVisible) StatusText.Text = pinned ? Loc.T("Pinned — double-click or Enter pastes into the window you used last; drag entries out.") : StatusText.Text;
    }

    /// <summary>
    /// Shows or hides the preview column; without it the window is just the list. The window glides wider or
    /// narrower with its left edge kept, so the list never moves, while the column fades in or out beside it.
    /// </summary>
    private void SetPreview(bool on, bool animate)
    {
        PreviewOn = on;
        IconButton.Set(_previewButton, on, null, on ? Loc.T("Hide the preview") : Loc.T("Show the preview"));
        int toggle = ++_previewToggles;
        animate &= IsVisible;
        if (on)
        {
            PreviewPanel.Visibility = Visibility.Visible;
            ShowPreview((Results.SelectedItem as ClipRow)?.Item);
            if (animate) { Motion.Reveal(PreviewPanel); Motion.GlideWidth(this, FullWidth); }
            else { PreviewPanel.BeginAnimation(OpacityProperty, null); Width = FullWidth; }
            return;
        }
        ReleaseFilePreview(); // a document's preview is another window: it cannot fade or be cropped, so it goes first
        PreviewFile.Visibility = Visibility.Collapsed;
        void Closed()
        {
            if (toggle != _previewToggles) return; // opened again meanwhile
            PreviewPanel.Visibility = Visibility.Collapsed;
            PreviewPanel.BeginAnimation(OpacityProperty, null);
        }
        if (animate) Motion.GlideWidth(this, ListWidth, Closed); // the edge sweeps over the column, like a drawer shutting
        else { Width = ListWidth; Closed(); }
    }

    private void HideUnlessPinned()
    {
        if (!_pinned) Hide();
    }

    /// <summary>The search icon and the footer move the window.</summary>
    private void OnMoveAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        e.Handled = true;
        try { DragMove(); } catch (InvalidOperationException) { }
        QueryBox.Focus();
    }

    /// <summary>The hotkey pressed again while open: close and type the character the hotkey swallowed.</summary>
    public async void DismissAndType(string text)
    {
        Hide();
        if (_previous != IntPtr.Zero && IsWindow(_previous)) SetForegroundWindow(_previous);
        await Task.Delay(40);
        await KeyboardOutput.WaitForModifiersReleasedAsync(400);
        KeyboardOutput.TypeText(text);
    }

    // ---- List ---------------------------------------------------------------------------------------

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Spaces around the words change nothing: do not refilter (that jumped back to the first entry and flashed).
        string query = QueryBox.Text.Trim();
        if (query == _lastQuery) return;
        _lastQuery = query;
        Refresh(keepSelection: false);
    }

    private void Refresh(bool keepSelection)
    {
        string? selectedId = keepSelection ? (Results.SelectedItem as ClipRow)?.Item.Id : null;
        var rows = _history.Search(QueryBox.Text, _filter).Take(300).Select(ReuseOrMakeRow).ToList();
        SyncRows(rows);
        int index = selectedId == null ? 0 : Math.Max(0, rows.FindIndex(r => r.Item.Id == selectedId));
        if (rows.Count == 0) Results.SelectedIndex = -1;
        else if (Results.SelectedIndex != index) Results.SelectedIndex = index; // the same row stays selected: no preview reload
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _history.Items.Count == 0 ? Loc.T("Nothing copied yet.\nText, images and files you copy show up here.") : Loc.T("Nothing matches.");
        int pinned = _history.Items.Count(i => i.Pinned);
        int count = _history.Items.Count;
        string entries = count == 1 ? Loc.F("{0:N0} entry", count) : Loc.F("{0:N0} entries", count);
        StatusText.Text = pinned > 0 ? Loc.F("{0} · {1} pinned", entries, pinned) : entries;
        if (rows.Count == 0) ShowPreview(null);
    }

    /// <summary>
    /// Updates the list in place: rows that did not change keep their objects (and their containers), so a new entry
    /// slides in at the top instead of every row being rebuilt — no flashing while the clipboard changes.
    /// </summary>
    private void SyncRows(List<ClipRow> rows)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (i < _rows.Count && ReferenceEquals(_rows[i], rows[i])) continue;
            int at = -1;
            for (int j = i + 1; j < _rows.Count; j++) if (ReferenceEquals(_rows[j], rows[i])) { at = j; break; }
            if (at > i) _rows.Move(at, i);
            else _rows.Insert(i, rows[i]);
        }
        while (_rows.Count > rows.Count) _rows.RemoveAt(_rows.Count - 1);
    }

    private ClipRow ReuseOrMakeRow(ClipItem item)
    {
        var old = _rows.FirstOrDefault(r => ReferenceEquals(r.Item, item));
        var fresh = MakeRow(item);
        return old != null && old.PinnedWhenMade == item.Pinned && old.Title == fresh.Title && old.Subtitle == fresh.Subtitle ? old : fresh;
    }

    private ClipRow MakeRow(ClipItem item)
    {
        string title = item.Kind switch
        {
            ClipKind.Image => Loc.F("Image {0} × {1}", item.ImageWidth, item.ImageHeight),
            ClipKind.Files => item.Files.Length == 1 ? Path.GetFileName(item.Files[0]) : Loc.F("{0} files: {1}", item.Files.Length, string.Join(", ", item.Files.Take(3).Select(Path.GetFileName))),
            _ => OneLine(item.Text),
        };
        string detail = item.Kind switch
        {
            ClipKind.Text => item.Text.Length == 1 ? Loc.F("{0:N0} character", item.Text.Length) : Loc.F("{0:N0} characters", item.Text.Length),
            ClipKind.Files => Path.GetDirectoryName(item.Files.FirstOrDefault() ?? "") ?? "",
            _ => "",
        };
        var parts = new[] { item.Source, Ago(item.Created), detail }.Where(p => p.Length > 0);
        var row = new ClipRow { Item = item, Title = title, Subtitle = string.Join(" · ", parts), PinnedWhenMade = item.Pinned };
        if (item.Kind == ClipKind.Image) row.Thumbnail = Thumbnail(item.Id, _history.ImagePath(item));
        else if (SinglePicture(item) is { } picture) row.Thumbnail = Thumbnail(item.Id, picture);
        return row;
    }

    private static string OneLine(string text)
    {
        var span = text.AsSpan().Trim();
        if (span.Length > 300) span = span[..300];
        var sb = new System.Text.StringBuilder(span.Length);
        bool space = false;
        foreach (char c in span)
        {
            if (char.IsWhiteSpace(c)) { if (!space) sb.Append(' '); space = true; }
            else { sb.Append(c); space = false; }
        }
        return sb.ToString();
    }

    internal static string Ago(DateTime when)
    {
        var d = DateTime.Now - when;
        if (d.TotalMinutes < 1) return Loc.T("just now");
        if (d.TotalHours < 1) return Loc.F("{0} min ago", (int)d.TotalMinutes);
        if (d.TotalDays < 1 && when.Date == DateTime.Today) return when.ToString("HH:mm");
        if (when.Date == DateTime.Today.AddDays(-1)) return Loc.F("yesterday {0:HH:mm}", when);
        return when.Year == DateTime.Now.Year ? when.ToString(Loc.Pick("MMM d", "M月d日")) : when.ToString("yyyy-MM-dd");

    }

    /// <summary>
    /// A row's small picture: from the cache, or decoded in the background (a large photo takes a while to read)
    /// and put on the row when ready, so switching tabs never waits for it.
    /// </summary>
    private ImageSource? Thumbnail(string id, string path)
    {
        if (_thumbs.TryGetValue(id, out var cached)) return cached;
        if (!_thumbsLoading.Add(id)) return null;
        Task.Run(async () =>
        {
            await ThumbnailSlots.WaitAsync();
            try { return LoadImage(path, 72); }
            finally { ThumbnailSlots.Release(); }
        }).ContinueWith(t =>
        {
            _thumbsLoading.Remove(id);
            var img = t.IsCompletedSuccessfully ? t.Result : null;
            _thumbs[id] = img;
            foreach (var row in _rows) if (row.Item.Id == id) row.Thumbnail = img;
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return null;
    }

    private readonly HashSet<string> _thumbsLoading = new();
    private static readonly SemaphoreSlim ThumbnailSlots = new(2);

    private static readonly HashSet<string> PictureExts = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic" };

    /// <summary>The one picture file an entry holds (copied from Explorer), if that is what it is.</summary>
    private static string? SinglePicture(ClipItem item) =>
        item is { Kind: ClipKind.Files, Files.Length: 1 } && PictureExts.Contains(Path.GetExtension(item.Files[0])) && File.Exists(item.Files[0]) ? item.Files[0] : null;

    private static BitmapImage? LoadImage(string path, int decodeWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException) { return null; }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowPreview((Results.SelectedItem as ClipRow)?.Item);
        UpdateTranslateButton();
        if (Results.SelectedItem != null) Results.ScrollIntoView(Results.SelectedItem);
    }

    /// <summary>Ctrl+T and the search bar's button: pictures have their text read, text entries are translated.</summary>
    private void TranslateOrRecognize(ClipItem item)
    {
        if (Recognize != null && PictureOf(item) != null) RecognizeEntry(item);
        else if (CanTranslate(item)) _ = ToggleTranslationAsync(item);
    }

    /// <summary>The button shows only for entries it can act on (hidden, not collapsed, so the others stay put).</summary>
    private void UpdateTranslateButton()
    {
        var item = (Results.SelectedItem as ClipRow)?.Item;
        bool picture = item != null && Recognize != null && PictureOf(item) != null;
        bool text = item != null && CanTranslate(item);
        bool shown = text && _translation?.Id == item!.Id;
        _translateButton.Visibility = picture || text ? Visibility.Visible : Visibility.Hidden;
        IconButton.Set(_translateButton, shown, null,
            picture ? Loc.T("Recognise text (Ctrl+T)") : shown ? Loc.T("Hide translation (Ctrl+T)") : Loc.T("Translate (Ctrl+T)"));
    }

    private void ShowPreview(ClipItem? item)
    {
        if (!PreviewOn) return;
        string? file = item is { Kind: ClipKind.Files, Files.Length: 1 } && (File.Exists(item.Files[0]) || Directory.Exists(item.Files[0])) ? item.Files[0] : null;
        PreviewFile.Visibility = file != null ? Visibility.Visible : Visibility.Collapsed;
        PreviewText.Visibility = item is { Kind: not ClipKind.Image } && file == null ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Visibility = item is { Kind: ClipKind.Image } ? Visibility.Visible : Visibility.Collapsed;
        if (file != null)
        {
            if (_filePanePath == file) return;
            _pendingFile = file;
            _fileDebounce.Stop();
            _fileDebounce.Start();
            return;
        }
        _fileDebounce.Stop();
        ReleaseFilePreview();
        ShowTranslationPane(item != null && _translation?.Id == item.Id && PreviewText.Visibility == Visibility.Visible);
        int load = ++_imageLoads;
        if (item == null) { PreviewText.Text = ""; PreviewImage.Source = null; return; }
        if (item.Kind == ClipKind.Image)
        {
            // Decoded off the UI thread (a big screenshot takes a moment); the old picture stays until then.
            string path = _history.ImagePath(item);
            int width = Math.Min(item.ImageWidth, 1200);
            Task.Run(() => LoadImage(path, width)).ContinueWith(t =>
            {
                if (load != _imageLoads) return;
                PreviewImage.Source = t.Result;
                Motion.FadeIn(PreviewImage);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        PreviewText.Text = item.Text.Length > 50_000 ? item.Text[..50_000] + "\n…" : item.Text;
        PreviewText.ScrollToHome();
        if (_translation is { } t && t.Id == item.Id)
        {
            TranslationText.Text = t.Result?.Text ?? "";
            TranslationText.ScrollToHome();
            TranslationTitle.Text = t.Result is not { } r ? Loc.T("Translating…")
                : string.IsNullOrEmpty(r.Source)
                ? Loc.F("Translation · {0} · {1}", TranslationNames.Language(r.Target), TranslationNames.Engine(r.Engine))
                : Loc.F("Translation · {0} → {1} · {2}", TranslationNames.Language(r.Source), TranslationNames.Language(r.Target), TranslationNames.Engine(r.Engine));
        }
    }

    private void LoadPendingFile()
    {
        if (_pendingFile is not { } file || !IsVisible || (Results.SelectedItem as ClipRow)?.Item.Files.FirstOrDefault() != file) return;
        _filePane ??= new Preview.PreviewPane { Inline = true };
        _filePane.Load(file);
        _filePanePath = file;
        PreviewFile.Content = _filePane;
    }

    /// <summary>The picture an entry holds: a copied image, or one copied picture file.</summary>
    private string? PictureOf(ClipItem item) => item.Kind == ClipKind.Image && File.Exists(_history.ImagePath(item)) ? _history.ImagePath(item) : SinglePicture(item);

    private void RecognizeEntry(ClipItem item)
    {
        if (Recognize == null || PictureOf(item) is not { } path || LoadImage(path, 0) is not { } image) return;
        HideUnlessPinned();
        Recognize(image);
    }

    /// <summary>What Quick Look can open for an entry: the picture's PNG, or the single copied file.</summary>
    private string? QuickLookPath(ClipItem item) => item switch
    {
        { Kind: ClipKind.Image } when File.Exists(_history.ImagePath(item)) => _history.ImagePath(item),
        { Kind: ClipKind.Files, Files.Length: 1 } when File.Exists(item.Files[0]) || Directory.Exists(item.Files[0]) => item.Files[0],
        _ => null,
    };

    /// <summary>Stops whatever the file preview was doing (video, a document's preview handler).</summary>
    private void ReleaseFilePreview()
    {
        _filePane?.Release();
        _filePanePath = null;
        _pendingFile = null;
        PreviewFile.Content = null;
    }

    private TextBlock MakeChip(string text, int index)
    {
        var chip = new TextBlock { Text = text, Style = (Style)FindResource("FilterChip") };
        chip.MouseLeftButtonUp += (_, _) => { _filter = (ClipFilter)index; UpdateChips(); Refresh(false); QueryBox.Focus(); };
        return chip;
    }

    private void UpdateChips()
    {
        for (int i = 0; i < _chips.Length; i++)
        {
            bool on = (int)_filter == i;
            Launcher.FilterTab.Set(_chips[i], on);
        }
    }

    // ---- Keys ----------------------------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        var row = Results.SelectedItem as ClipRow;
        if (PreviewText.IsKeyboardFocusWithin && key != Key.Escape)
        {
            // Selecting in the preview: its own keys (Ctrl+C copies the selection, Ctrl+A, arrows, Shift+arrows) stay
            // there; anything else goes back to the search box.
            bool own = mods.HasFlag(ModifierKeys.Control) && key is Key.C or Key.A or Key.Insert
                       || key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown
                       || key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl;
            if (own) return;
            QueryBox.Focus();
        }
        switch (key)
        {
            case Key.Escape: Hide(); break;
            case Key.Space when QueryBox.Text.Length == 0 && mods == ModifierKeys.None && row != null && QuickLookPath(row.Item) is { } look && QuickLook != null:
                QuickLook(look); // as in Explorer: Space looks at the picture or file
                break;
            case Key.Down: Move(1); break;
            case Key.Up: Move(-1); break;
            case Key.PageDown: Move(8); break;
            case Key.PageUp: Move(-8); break;
            case Key.Tab:
                _filter = (ClipFilter)(((int)_filter + (mods.HasFlag(ModifierKeys.Shift) ? FilterNames.Length - 1 : 1)) % FilterNames.Length);
                UpdateChips();
                Refresh(false);
                break;
            case Key.Enter when row != null && !mods.HasFlag(ModifierKeys.Shift) && TranslationOf(row.Item) is { } translated:
                _ = PasteTextAsync(translated);
                break;
            case Key.Enter when row != null:
                _ = PasteAsync(row.Item, plainText: mods.HasFlag(ModifierKeys.Shift));
                break;
            case Key.C when mods == ModifierKeys.Control && row != null && QueryBox.SelectionLength == 0 && TranslationOf(row.Item) is { } translated:
                if (PutText(translated)) HideUnlessPinned();
                break;
            case Key.C when mods == ModifierKeys.Control && row != null && QueryBox.SelectionLength == 0:
                if (Put(row.Item, plainText: false)) { _history.Touch(row.Item.Id); HideUnlessPinned(); }
                break;
            case Key.T when mods == ModifierKeys.Control && row != null:
                TranslateOrRecognize(row.Item);
                break;
            case Key.P when mods == ModifierKeys.Control && row != null:
                _history.TogglePin(row.Item.Id);
                break;
            case Key.Delete when row != null && (QueryBox.Text.Length == 0 || mods == ModifierKeys.Shift):
                _history.Remove(row.Item.Id);
                break;
            case >= Key.D1 and <= Key.D9 when mods == ModifierKeys.Control:
                int index = key - Key.D1;
                if (Results.Items.Count > index && Results.Items[index] is ClipRow r) _ = PasteAsync(r.Item, false);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Move(int delta)
    {
        int count = Results.Items.Count;
        if (count == 0) return;
        int i = Results.SelectedIndex + delta;
        if (Math.Abs(delta) == 1) i = (i + count) % count;
        Results.SelectedIndex = Math.Clamp(i, 0, count - 1);
    }

    /// <summary>
    /// A click selects (and previews); a double-click pastes. Rows are not focusable (typing stays in the search
    /// box), so the list would not select on its own.
    /// </summary>
    private void OnResultMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject) is not { } d || ItemsControl.ContainerFromElement(Results, d) is not ListBoxItem { DataContext: ClipRow row }) return;
        e.Handled = true;
        Results.SelectedItem = row;
        QueryBox.Focus();
        if (e.ClickCount >= 2) { _dragRow = null; _ = PasteAsync(row.Item, false); return; }
        _dragStart = e.GetPosition(Results);
        _dragRow = row;
    }

    /// <summary>Dragging a row out drops its text, picture or files into the window under the mouse.</summary>
    private void OnResultMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragRow is not { } row || e.LeftButton != MouseButtonState.Pressed) { _dragRow = null; return; }
        var delta = e.GetPosition(Results) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragRow = null;
        var data = DataFor(row.Item, plainText: false, forDrag: true);
        if (data == null) return;
        _history.Touch(row.Item.Id);
        try { DragDrop.DoDragDrop(Results, data, DragDropEffects.Copy); }
        catch (COMException ex) { Log.Debug(ex, "Drag from the clipboard history failed"); }
    }

    // ---- Paste ----------------------------------------------------------------------------------------

    private async Task PasteAsync(ClipItem item, bool plainText)
    {
        if (!Put(item, plainText)) return;
        _history.Touch(item.Id);
        var target = _previous;
        HideUnlessPinned();
        if (target == IntPtr.Zero || !IsWindow(target) || target == new WindowInteropHelper(this).Handle) return;
        SetForegroundWindow(target);
        await Task.Delay(60);
        await KeyboardOutput.WaitForModifiersReleasedAsync();
        KeyboardOutput.SendCtrlV();
    }

    /// <summary>
    /// An entry as clipboard or drag data. Dragged pictures also carry their PNG file, which chat apps and folders
    /// take as a drop.
    /// </summary>
    private DataObject? DataFor(ClipItem item, bool plainText, bool forDrag)
    {
        var data = new DataObject();
        switch (item.Kind)
        {
            case ClipKind.Files when !plainText:
                var files = new StringCollection();
                files.AddRange(item.Files);
                data.SetFileDropList(files);
                break;
            case ClipKind.Image:
                string path = _history.ImagePath(item);
                var bmp = LoadImage(path, 0);
                if (bmp == null) return null;
                data.SetImage(bmp);
                try { data.SetData("PNG", new MemoryStream(File.ReadAllBytes(path))); } catch (IOException) { }
                if (forDrag && File.Exists(path)) data.SetFileDropList(new StringCollection { path });
                break;
            default:
                data.SetText(item.Text, TextDataFormat.UnicodeText);
                break;
        }
        return data;
    }

    // ---- Translation ---------------------------------------------------------------------------------

    private bool CanTranslate(ClipItem item) => Translate != null && item.Kind == ClipKind.Text && item.Text.Trim().Length > 0;

    /// <summary>The translation shown for this entry, if any (Enter and Ctrl+C then use it).</summary>
    /// <summary>The service or target language changed: earlier translations are asked for again.</summary>
    public void ForgetTranslations() => _translated.Clear();

    private string? TranslationOf(ClipItem item) => _translation is { } t && t.Id == item.Id ? t.Result?.Text : null;

    /// <summary>
    /// Shows the entry's translation under its text in the preview, or hides it again. The pane opens at once
    /// ("Translating…") and fills in when the service answers.
    /// </summary>
    private async Task ToggleTranslationAsync(ClipItem item)
    {
        if (Translate == null) return;
        _translating?.Cancel();
        if (_translation?.Id == item.Id)
        {
            _translation = null;
            ShowPreview(item);
            UpdateTranslateButton();
            StatusText.Text = Loc.T("Translation hidden.");
            return;
        }
        if (_translated.TryGetValue(item.Id, out var known))
        {
            // Translated before (hidden since): show it again without asking the service.
            _translation = (item.Id, known);
            if (!PreviewOn) SetPreview(true, animate: true);
            else ShowPreview(item);
            UpdateTranslateButton();
            StatusText.Text = Loc.T("Enter pastes the translation · Shift+Enter the original · Ctrl+T hides the translation");
            return;
        }
        var cts = _translating = new CancellationTokenSource();
        _translation = (item.Id, null);
        if (!PreviewOn) SetPreview(true, animate: true);
        else ShowPreview(item);
        UpdateTranslateButton();
        StatusText.Text = Loc.T("Translating…");
        try
        {
            var result = await Translate(item.Text, cts.Token);
            if (cts.IsCancellationRequested) return;
            _translation = (item.Id, result);
            if (_translated.Count >= 200) _translated.Clear();
            _translated[item.Id] = result;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Translating a clipboard entry failed");
            if (cts.IsCancellationRequested) return;
            _translation = null;
            if ((Results.SelectedItem as ClipRow)?.Item.Id == item.Id) ShowPreview(item);
            UpdateTranslateButton();
            StatusText.Text = Loc.T("Could not reach a translation service. Check the connection and try again.");
            return;
        }
        if ((Results.SelectedItem as ClipRow)?.Item.Id == item.Id) ShowPreview(item);
        UpdateTranslateButton();
        StatusText.Text = Loc.T("Enter pastes the translation · Shift+Enter the original · Ctrl+T hides the translation");
    }

    /// <summary>The translation under the text, sharing the preview half and half.</summary>
    private void ShowTranslationPane(bool show)
    {
        if (show == (TranslationPane.Visibility == Visibility.Visible)) return;
        TranslationPane.Visibility = TranslationSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TranslationRow.Height = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TranslationRow.MinHeight = show ? 60 : 0;
        if (PreviewText.Parent is Grid g) g.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
    }

    private async Task PasteTextAsync(string text)
    {
        if (!PutText(text)) return;
        var target = _previous;
        HideUnlessPinned();
        if (target == IntPtr.Zero || !IsWindow(target) || target == new WindowInteropHelper(this).Handle) return;
        SetForegroundWindow(target);
        await Task.Delay(60);
        await KeyboardOutput.WaitForModifiersReleasedAsync();
        KeyboardOutput.SendCtrlV();
    }

    /// <summary>Puts plain text on the clipboard; the monitor records it like any copy.</summary>
    private bool PutText(string text)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        return SetClipboard(data);
    }

    /// <summary>Puts an entry on the clipboard (not recorded again by the monitor).</summary>
    private bool Put(ClipItem item, bool plainText)
    {
        if (DataFor(item, plainText, forDrag: false) is not { } data) return false;
        _monitor.IgnoreNextChange();
        return SetClipboard(data);
    }

    private static bool SetClipboard(DataObject data)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (COMException) { Thread.Sleep(30); }
        }
        Log.Warning("Could not put a clipboard history entry on the clipboard");
        return false;
    }
}
