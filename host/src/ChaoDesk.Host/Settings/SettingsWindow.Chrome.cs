using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ChaoDesk.Host.Localization;

namespace ChaoDesk.Host.Settings;

/// <summary>The window's own title bar buttons, and the big keycaps that show (and record) a page's hotkey.</summary>
public partial class SettingsWindow
{
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        bool max = WindowState == WindowState.Maximized;
        // A maximised window with its own chrome reaches past the screen edges by the frame width; keep content inside.
        RootGrid.Margin = max ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = max ? "" : "";
    }

    // ---- Launcher page tabs -------------------------------------------------------------------------------

    private void OnLauncherTab(object sender, RoutedEventArgs e)
    {
        if (LauncherHelpView == null) return; // during InitializeComponent
        bool help = LauncherTabHelp.IsChecked == true;
        LauncherHelpView.Visibility = help ? Visibility.Visible : Visibility.Collapsed;
        LauncherSettingsView.Visibility = help ? Visibility.Collapsed : Visibility.Visible;
    }


    // ---- Big keycaps --------------------------------------------------------------------------------------

    /// <summary>
    /// Shows a hotkey box's text as large keycaps in <paramref name="caps"/>; the (transparent) box above them still
    /// takes the click and records the keys, and <paramref name="frame"/> outlines them while it listens.
    /// </summary>
    private void WireBigKeycaps(TextBox box, System.Windows.Controls.Panel caps, Border frame)
    {
        box.TextChanged += (_, _) => RenderBigKeycaps(caps, box.Text);
        box.GotKeyboardFocus += (_, _) => frame.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        box.LostKeyboardFocus += (_, _) => frame.BorderBrush = Brushes.Transparent;
        RenderBigKeycaps(caps, box.Text);
    }

    private void RenderBigKeycaps(System.Windows.Controls.Panel caps, string text)
    {
        caps.Children.Clear();
        // "Ctrl+Alt+A" → three caps; anything else ("Press keys…", "Off") → one wide cap with the words.
        bool combination = text.Contains('+') && !text.Contains(' ');
        var keys = combination ? text.Split('+', StringSplitOptions.RemoveEmptyEntries) : [text];
        for (int i = 0; i < keys.Length; i++)
        {
            if (i > 0) caps.Children.Add(new TextBlock { Text = "+", FontSize = 15, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("PlaceholderBrush") });
            caps.Children.Add(BigKeycap(keys[i], wide: keys[i] is "Space" || !combination));
        }
    }

    private Border BigKeycap(string key, bool wide)
    {
        var label = new TextBlock { Text = key, FontSize = 15, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var cap = new Border
        {
            MinWidth = wide ? 120 : 50, Height = 46, Padding = new Thickness(16, 0, 16, 0), CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1, 1, 1, 3), Child = label,
        };
        cap.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        cap.SetResourceReference(Border.BorderBrushProperty, "ToggleTrackBrush");
        return cap;
    }
}
