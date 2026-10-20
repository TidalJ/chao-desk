using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using ChaoDesk.Host.Launcher;
using ChaoDesk.Host.Localization;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Preview;

/// <summary>
/// Quick Look: a <see cref="PreviewPane"/> in its own window (Space in Explorer). Space or Esc closes, Enter opens,
/// ← → go through the folder, Ctrl+C copies the file.
/// </summary>
public sealed class QuickLookWindow : Window
{
    private readonly PreviewPane _pane = new();
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _details = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Image _icon = new() { Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _textButton;

    public string CurrentPath => _pane.CurrentPath;

    public QuickLookWindow()
    {
        Title = Loc.T("Quick Look");
        Width = 1000;
        Height = 700;
        MinWidth = 360;
        MinHeight = 240;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = true;
        Topmost = true;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        SetResourceReference(BackgroundProperty, "PaperBrush");
        SetResourceReference(ForegroundProperty, "InkBrush");
        _details.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 40, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0, 0, 0, 1), UseAeroCaptionButtons = false });

        var bar = new DockPanel { Height = 40, Margin = new Thickness(14, 0, 6, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _textButton = BarButton("", Loc.T("Show the text only (T)"), () => { _pane.ShowText(); UpdateHeader(); });
        buttons.Children.Add(_textButton);
        buttons.Children.Add(BarButton("", Loc.T("Open with the default app (Enter)"), OpenExternally));
        buttons.Children.Add(BarButton("", Loc.T("Show in folder"), () => { Reveal(CurrentPath); Close(); }));
        buttons.Children.Add(BarButton("", Loc.T("Close (Space, Esc)"),
 Close));
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        DockPanel.SetDock(_icon, Dock.Left);
        bar.Children.Add(_icon);
        var titles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(_title);
        titles.Children.Add(_details);
        bar.Children.Add(titles);

        var root = new DockPanel();
        var barBorder = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        barBorder.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        DockPanel.SetDock(barBorder, Dock.Top);
        root.Children.Add(barBorder);
        root.Children.Add(_pane);
        Content = root;

        _pane.DetailsChanged += () => _details.Text = _pane.Details;
        _pane.CloseRequested += Close;
        SourceInitialized += (_, _) =>
        {
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        };
        PreviewKeyDown += OnKey;
        Closed += (_, _) => _pane.Release();
    }

    internal static Border BarButton(string glyph, string tip, Action action)
    {
        var b = new Border
        {
            Width = 36, Height = 30, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, ToolTip = tip, Cursor = Cursors.Hand,
            Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => action();
        return b;
    }

    /// <summary>Shows <paramref name="path"/>, sized for its content and centred on the screen under the mouse.</summary>
    public void ShowPath(string path)
    {
        Load(path);
        if (!IsVisible)
        {
            CenterOnMouseMonitor();
            Show();
        }
        Activate();
        Focus();
    }

    private void Load(string path)
    {
        _pane.Load(path);
        UpdateHeader();
        if (_pane.ImageSize is { } px)
        {
            // Size the window to the picture (at most 85 % of the screen).
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var work = SystemParameters.WorkArea;
            double w = px.Width / dpi, h = px.Height / dpi;
            double fit = Math.Min(1, Math.Min(work.Width * 0.85 / w, (work.Height * 0.85 - 40) / h));
            Width = Math.Max(MinWidth, w * fit);
            Height = Math.Max(MinHeight, h * fit + 41);
        }
    }

    private void UpdateHeader()
    {
        string path = CurrentPath;
        _title.Text = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;
        _icon.Source = ShellIcons.ForPath(path);
        _details.Text = _pane.Details;
        _textButton.Visibility = _pane.CanShowText ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CenterOnMouseMonitor()
    {
        GetCursorPos(out POINT cursor);
        var monitor = Desktop.MonitorInfo.GetAll().FirstOrDefault(m => m.Bounds.Contains(cursor.X, cursor.Y));
        if (monitor == null) { WindowStartupLocation = WindowStartupLocation.CenterScreen; return; }
        var work = monitor.WorkArea;
        double s = monitor.Scale;
        Width = Math.Min(Width, work.Width / s * 0.9);
        Height = Math.Min(Height, work.Height / s * 0.9);
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Left/Top are DIPs relative to the primary monitor's scale; good enough for a first placement.
        Left = work.X / s + (work.Width / s - Width) / 2;
        Top = work.Y / s + (work.Height / s - Height) / 2;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox { IsReadOnly: false }) return;
        var mods = Keyboard.Modifiers;
        bool inText = e.OriginalSource is TextBox or ListBoxItem;
        switch (e.Key)
        {
            case Key.Escape:
            case Key.Space when mods == ModifierKeys.None:
                Close();
                break;
            case Key.Enter:
                OpenExternally();
                break;
            case Key.T when mods == ModifierKeys.None && _pane.CanShowText && !inText:
                _pane.ShowText();
                UpdateHeader();
                break;
            case Key.Left or Key.Up when mods == ModifierKeys.None && !inText:
                Step(-1);
                break;
            case Key.Right or Key.Down when mods == ModifierKeys.None && !inText:
                Step(1);
                break;
            case Key.C when mods == ModifierKeys.Control && e.OriginalSource is not TextBox:
                try { Clipboard.SetFileDropList(new StringCollection { CurrentPath }); } catch (COMException) { }
                break;
            case Key.D0 when mods == ModifierKeys.Control: _pane.ResetZoom(); break;
            case Key.OemPlus or Key.Add when mods == ModifierKeys.Control: _pane.Zoom(1.25); break;
            case Key.OemMinus or Key.Subtract when mods == ModifierKeys.Control: _pane.Zoom(0.8); break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>The previous or next entry in the same folder (Explorer's name order, folders first).</summary>
    private void Step(int delta)
    {
        if (Siblings.Next(CurrentPath, delta) is { } next) Load(next);
    }

    private void OpenExternally()
    {
        try { Process.Start(new ProcessStartInfo(CurrentPath) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Information(ex, "Could not open {Path}", CurrentPath); return; }
        Close();
    }

    internal static void Reveal(string path) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    /// <summary>Kept for the launcher, which formats sizes the same way.</summary>
    public static string FormatSize(long bytes) => PreviewPane.FormatSize(bytes);
}

/// <summary>Neighbours of a file in its folder, in Explorer's order (folders first, natural name sort, no hidden files).</summary>
internal static class Siblings
{
    public static string? Next(string path, int delta)
    {
        string? dir = Path.GetDirectoryName(path.TrimEnd('\\'));
        if (dir == null) return null;
        try
        {
            var all = Directory.EnumerateFileSystemEntries(dir)
                .Where(p => (File.GetAttributes(p) & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderByDescending(Directory.Exists).ThenBy(Path.GetFileName, Comparer<string?>.Create((a, b) => StrCmpLogicalW(a ?? "", b ?? "")))
                .ToList();
            int i = all.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            return i < 0 || all.Count < 2 ? null : all[(i + delta + all.Count) % all.Count];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
