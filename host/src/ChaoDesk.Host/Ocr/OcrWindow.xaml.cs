using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ChaoDesk.Host.Localization;
using ChaoDesk.Host.Translate;
using Serilog;

namespace ChaoDesk.Host.Ocr;

/// <summary>
/// The text found in a picture: the picture on the left with a box over every recognised word — drag over it to pick
/// words, as with Live Text — and the text on the right, editable. Picking words on the picture selects them in the
/// text too (until the text is edited); Ctrl+C copies them in reading order.
/// </summary>
public partial class OcrWindow : Window
{
    private static readonly Brush WordFill = new SolidColorBrush(Color.FromArgb(0x1C, 0x4C, 0x5B, 0xD4));
    private static readonly Brush PickedFill = new SolidColorBrush(Color.FromArgb(0x66, 0x4C, 0x5B, 0xD4));

    private readonly BitmapSource _image;
    private readonly Action<BitmapSource>? _pin;
    private readonly Action<string, bool, bool> _remember; // language, join lines, translate automatically
    private readonly List<string> _languageTags = new();
    private readonly HashSet<(int Line, int Word)> _picked = new();
    private readonly List<(int Line, int Word, Rectangle Box)> _boxes = new();
    private RecognizedText? _result;
    private List<WordSpan> _spans = new();
    private CancellationTokenSource? _cts;
    private bool _composing, _edited, _loading;
    private Point? _dragStart;
    private HashSet<(int, int)> _pickedBeforeDrag = new();
    private CancellationTokenSource? _translating;
    private (string Source, Translation Result)? _translation; // the last translation and the text it was made from
    private readonly System.Windows.Threading.DispatcherTimer _retranslate; // automatic: after edits settle

    /// <summary>Translates text with the user's service and target (null: no Translate button).</summary>
    public Func<string, CancellationToken, Task<Translation>>? Translate { get; init; }

    public OcrWindow(BitmapSource image, string language, bool mergeLines, bool autoTranslate, Action<BitmapSource>? pin, Action<string, bool, bool> remember)
    {
        _image = image;
        _pin = pin;
        _remember = remember;
        InitializeComponent();
        TextOut.IsInactiveSelectionHighlightEnabled = true;
        PinButton.Visibility = pin != null ? Visibility.Visible : Visibility.Collapsed;
        PictureImage.Source = image;
        PictureImage.Width = Picture.Width = Boxes.Width = image.PixelWidth;
        PictureImage.Height = Picture.Height = Boxes.Height = image.PixelHeight;
        SizeToPicture();

        _loading = true;
        var packs = OcrPacks.Installed;
        var names = new List<string> { packs.Count > 1 ? Loc.T("All installed languages") : OcrPacks.BuiltIn.Name };
        _languageTags.Add("auto");
        if (packs.Count > 1)
            foreach (var p in packs) { names.Add(Loc.F("{0} only", p.Name)); _languageTags.Add(p.Id); }
        LanguageCombo.ItemsSource = names;
        LanguageCombo.SelectedIndex = Math.Max(0, _languageTags.FindIndex(t => t.Equals(language, StringComparison.OrdinalIgnoreCase)));
        MergeCheck.IsChecked = mergeLines;
        AutoTranslateCheck.IsChecked = autoTranslate;
        _loading = false;
        _retranslate = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _retranslate.Tick += (_, _) => { _retranslate.Stop(); _ = TranslateNowAsync(); };

        PreviewKeyDown += OnKey;
        Closed += (_, _) => { _cts?.Cancel(); _translating?.Cancel(); _retranslate.Stop(); };
        Loaded += (_, _) =>
        {
            // Without a translation service there is nothing to switch on.
            if (Translate == null) AutoTranslateCheck.Visibility = AutoTranslateLabel.Visibility = Visibility.Collapsed;
            _ = RecognizeAsync();
        };
    }

    private string RecognitionLanguage => _languageTags[Math.Max(0, LanguageCombo.SelectedIndex)];

    /// <summary>A window that fits the picture (and room for the text) without being larger than needed.</summary>
    private void SizeToPicture()
    {
        var area = SystemParameters.WorkArea;
        double picW = Math.Min(_image.PixelWidth, area.Width * 0.5), picH = Math.Min(_image.PixelHeight, area.Height * 0.6);
        Width = Math.Clamp(picW + 32 + Math.Max(380, picW * 0.8), 640, area.Width * 0.9);
        Height = Math.Clamp(picH + 32 + 100, 380, area.Height * 0.85);
    }

    // ---- Recognition ---------------------------------------------------------------------------------------

    private async Task RecognizeAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        BusyText.Text = Loc.T("Recognising…");
        BusyText.Visibility = Visibility.Visible;
        StatusText.Text = "";
        try
        {
            var result = await TextRecognizer.RecognizeAsync(_image, RecognitionLanguage, cts.Token);
            if (cts.IsCancellationRequested) return;
            _result = result;
            _picked.Clear();
            BuildBoxes();
            Compose();
            if (result.IsEmpty)
            {
                BusyText.Text = TextRecognizer.IsAvailable && PaddleOcr.LoadError == null
                    ? Loc.T("No text found in this picture.")
                    : PaddleOcr.LoadError ?? Loc.T("Text recognition is not available.");
                return;
            }
            BusyText.Visibility = Visibility.Collapsed;
            int chars = result.Lines.Sum(l => l.Words.Sum(w => w.Text.Length));
            string lines = result.Lines.Count == 1 ? Loc.F("{0:N0} line", result.Lines.Count) : Loc.F("{0:N0} lines", result.Lines.Count);
            string characters = chars == 1 ? Loc.F("{0:N0} character", chars) : Loc.F("{0:N0} characters", chars);
            StatusText.Text = $"{lines} · {characters} · {result.Engine} · {result.Milliseconds} ms";
            TranslateButton.IsEnabled = Translate != null;
            TextOut.Focus();
            TextOut.CaretIndex = 0;
            if (Translate != null && AutoTranslateCheck.IsChecked == true) await TranslateNowAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Text recognition failed");
            BusyText.Text = Loc.F("Text recognition failed: {0}", ex.Message);
        }
    }

    private void Compose()
    {
        if (_result == null) return;
        var (text, spans) = OcrLayout.Compose(_result, MergeCheck.IsChecked == true);
        _spans = spans;
        _composing = true;
        TextOut.Text = text;
        _composing = false;
        _edited = false;
        TextChangedSinceTranslation();
    }

    private void BuildBoxes()
    {
        Boxes.Children.Clear();
        _boxes.Clear();
        if (_result == null) return;
        for (int li = 0; li < _result.Lines.Count; li++)
            for (int wi = 0; wi < _result.Lines[li].Words.Count; wi++)
            {
                var r = _result.Lines[li].Words[wi].Bounds;
                var box = new Rectangle { Width = Math.Max(2, r.Width + 2), Height = Math.Max(2, r.Height + 2), RadiusX = 2, RadiusY = 2, Fill = WordFill };
                Canvas.SetLeft(box, r.Left - 1);
                Canvas.SetTop(box, r.Top - 1);
                Boxes.Children.Add(box);
                _boxes.Add((li, wi, box));
            }
    }

    private void Remember() => _remember(RecognitionLanguage, MergeCheck.IsChecked == true, AutoTranslateCheck.IsChecked == true);

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Remember();
        _ = RecognizeAsync();
    }

    private void OnMergeToggled(object sender, RoutedEventArgs e)
    {
        Remember();
        Compose();
        ShowPicked();
    }

    private void OnAutoTranslateToggled(object sender, RoutedEventArgs e)
    {
        Remember();
        if (AutoTranslateCheck.IsChecked == true && _result is { IsEmpty: false } && _translation?.Source != TextOut.Text) _ = TranslateNowAsync();
    }

    private void OnTextEdited(object sender, TextChangedEventArgs e)
    {
        if (_composing) return;
        _edited = true; // the spans no longer match: picking words still copies them, but cannot select them here
        TextChangedSinceTranslation();
    }

    // ---- Picking words on the picture ------------------------------------------------------------------------

    private (int Line, int Word)? WordAt(Point p)
    {
        foreach (var (line, word, _) in _boxes)
        {
            var r = _result!.Lines[line].Words[word].Bounds;
            r.Inflate(2, 2);
            if (r.Contains(p)) return (line, word);
        }
        return null;
    }

    private void OnPictureDown(object sender, MouseButtonEventArgs e)
    {
        if (_result == null) return;
        var p = e.GetPosition(Picture);
        if (e.ClickCount == 2 && WordAt(p) is { } hit)
        {
            // A double-click picks the whole line.
            _picked.Clear();
            for (int wi = 0; wi < _result.Lines[hit.Line].Words.Count; wi++) _picked.Add((hit.Line, wi));
            ShowPicked();
            return;
        }
        _dragStart = p;
        _pickedBeforeDrag = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? new HashSet<(int, int)>(_picked) : new();
        Picture.CaptureMouse();
        Picture.Focus();
        UpdateDrag(p);
    }

    private void OnPictureMove(object sender, MouseEventArgs e)
    {
        if (_dragStart != null && e.LeftButton == MouseButtonState.Pressed) UpdateDrag(e.GetPosition(Picture));
    }

    private void OnPictureUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart == null) return;
        _dragStart = null;
        Picture.ReleaseMouseCapture();
        DragBox.Visibility = Visibility.Collapsed;
    }

    /// <summary>Everything the drag rectangle touches is picked (a click picks the word under it).</summary>
    private void UpdateDrag(Point p)
    {
        if (_dragStart is not { } start || _result == null) return;
        var rect = new Rect(start, p);
        bool click = rect.Width < 3 && rect.Height < 3;
        DragBox.Visibility = click ? Visibility.Collapsed : Visibility.Visible;
        Canvas.SetLeft(DragBox, rect.Left);
        Canvas.SetTop(DragBox, rect.Top);
        DragBox.Width = rect.Width;
        DragBox.Height = rect.Height;
        _picked.Clear();
        _picked.UnionWith(_pickedBeforeDrag);
        if (click) { if (WordAt(p) is { } hit) _picked.Add(hit); }
        else
            foreach (var (line, word, _) in _boxes)
                if (_result.Lines[line].Words[word].Bounds.IntersectsWith(rect)) _picked.Add((line, word));
        ShowPicked();
    }

    /// <summary>Colours the picked words and selects them in the text (first to last, while the text is unedited).</summary>
    private void ShowPicked()
    {
        foreach (var (line, word, box) in _boxes) box.Fill = _picked.Contains((line, word)) ? PickedFill : WordFill;
        if (_edited || _picked.Count == 0) return;
        var spans = _spans.Where(s => _picked.Contains((s.Line, s.Word))).ToList();
        if (spans.Count == 0) return;
        int from = spans.Min(s => s.Start), to = spans.Max(s => s.Start + s.Length);
        TextOut.Select(from, to - from);
    }

    // ---- Keys and buttons -------------------------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.C when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                CopyAll();
                break;
            case Key.T when mods == ModifierKeys.Control && TranslateButton.IsEnabled:
                _ = ToggleTranslationAsync();
                break;
            case Key.C when mods == ModifierKeys.Control && _picked.Count > 0 && (!TextOut.IsKeyboardFocusWithin || TextOut.SelectionLength == 0 || !_edited):
                // Words picked on the picture: copy them in reading order (one line per line).
                Copy(OcrLayout.SelectionText(_result!, _picked), _picked.Count == 1 ? Loc.F("Copied {0} word.", _picked.Count) : Loc.F("Copied {0} words.", _picked.Count));
                break;
            case Key.A when mods == ModifierKeys.Control && Picture.IsKeyboardFocused && _result != null:
                _picked.Clear();
                foreach (var (line, word, _) in _boxes) _picked.Add((line, word));
                ShowPicked();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnCopyAll(object sender, RoutedEventArgs e) => CopyAll();

    private void CopyAll() => Copy(TextOut.Text, Loc.T("Copied all the text."));

    private void OnCopyTranslation(object sender, RoutedEventArgs e) => Copy(TranslationOut.Text, Loc.T("Copied the translation."));

    // ---- Translation: a pane under the text ---------------------------------------------------------------

    private bool TranslationShown => TranslationPane.Visibility == Visibility.Visible;

    private void OnTranslate(object sender, RoutedEventArgs e) => _ = ToggleTranslationAsync();

    private void OnRetranslate(object sender, RoutedEventArgs e) => _ = TranslateNowAsync();

    /// <summary>Ctrl+T / the button: show the translation (translating if needed), or hide the pane.</summary>
    private async Task ToggleTranslationAsync()
    {
        if (TranslationShown) { ShowTranslationPane(false); TextOut.Focus(); return; }
        if (_translation is { } cached && cached.Source == TextOut.Text) { ShowTranslation(cached.Result, stale: false); return; }
        await TranslateNowAsync();
    }

    /// <summary>Translates the text as it is now (edits included) and shows it under the text.</summary>
    private async Task TranslateNowAsync()
    {
        _retranslate.Stop();
        if (Translate == null || TextOut.Text.Trim().Length == 0) return;
        string text = TextOut.Text;
        _translating?.Cancel();
        var cts = _translating = new CancellationTokenSource();
        TranslateButton.IsEnabled = RetranslateButton.IsEnabled = false;
        StatusText.Text = Loc.T("Translating…");
        if (!TranslationShown) { TranslationTitle.Text = Loc.T("Translating…"); TranslationOut.Text = ""; ShowTranslationPane(true); }
        try
        {
            var result = await Translate(text, cts.Token);
            if (cts.IsCancellationRequested) return;
            _translation = (text, result);
            ShowTranslation(result, stale: TextOut.Text != text);
            StatusText.Text = Loc.F("Translated into {0} by {1}", TranslationNames.Language(result.Target), TranslationNames.Engine(result.Engine));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "Translating recognised text failed");
            StatusText.Text = Loc.T("Could not reach a translation service. Check the connection and try again.");
            if (_translation == null) ShowTranslationPane(false);
            else RetranslateButton.Visibility = Visibility.Visible;
        }
        finally
        {
            if (_translating == cts) TranslateButton.IsEnabled = RetranslateButton.IsEnabled = true;
        }
    }

    private void ShowTranslation(Translation t, bool stale)
    {
        TranslationOut.Text = t.Text;
        TranslationTitle.Text = string.IsNullOrEmpty(t.Source) || t.Source == "auto"
            ? Loc.F("Translation · {0} · {1}", TranslationNames.Language(t.Target), TranslationNames.Engine(t.Engine))
            : Loc.F("Translation · {0} → {1} · {2}", TranslationNames.Language(t.Source), TranslationNames.Language(t.Target), TranslationNames.Engine(t.Engine));
        RetranslateButton.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
        ShowTranslationPane(true);
    }

    /// <summary>The text changed (an edit, joining lines): translate again after a pause, or offer to.</summary>
    private void TextChangedSinceTranslation()
    {
        if (_translation == null || !TranslationShown) return;
        bool stale = _translation.Value.Source != TextOut.Text;
        if (stale && AutoTranslateCheck.IsChecked == true) { _retranslate.Stop(); _retranslate.Start(); }
        RetranslateButton.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows or hides the translation pane; shown, it shares the column with the text half and half.</summary>
    private void ShowTranslationPane(bool show)
    {
        if (show == TranslationShown) return;
        TranslationPane.Visibility = TranslationSplitter.Visibility = OriginalHeader.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OriginalRow.Height = new GridLength(1, GridUnitType.Star);
        TranslationRow.Height = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TranslationRow.MinHeight = show ? 80 : 0;
        TranslateButton.Content = Loc.T(show ? "Hide translation" : "Translate");
    }

    private void OnPin(object sender, RoutedEventArgs e) => _pin?.Invoke(_image);

    private void Copy(string text, string done)
    {
        if (text.Length == 0) return;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                StatusText.Text = done;
                return;
            }
            catch (COMException) { Thread.Sleep(30); }
        }
        StatusText.Text = Loc.T("The clipboard is busy; try again.");

    }
}
