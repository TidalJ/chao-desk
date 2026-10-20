using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ChaoDesk.Host.Capture;

/// <summary>A small notice in the bottom-right corner that fades out by itself (click runs an action).</summary>
public sealed class Toast : Window
{
    public static void Show(string title, string detail, ImageSource? image = null, Action? onClick = null)
    {
        var toast = new Toast(title, detail, image, onClick);
        toast.Show();
    }

    private Toast(string title, string detail, ImageSource? image, Action? onClick)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (image != null)
            row.Children.Add(new Border
            {
                Width = 64, Height = 44, Margin = new Thickness(0, 0, 12, 0), CornerRadius = new CornerRadius(4), ClipToBounds = true,
                Child = new Image { Source = image, Stretch = Stretch.UniformToFill },
            });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 320 };
        var titleText = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        texts.Children.Add(titleText);
        if (detail.Length > 0)
        {
            var detailText = new TextBlock { Text = detail, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
            detailText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            texts.Children.Add(detailText);
        }
        row.Children.Add(texts);
        var card = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(14, 10, 16, 10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.25 },
            Child = row,
            Cursor = onClick != null ? System.Windows.Input.Cursors.Hand : null,
        };
        card.SetResourceReference(Border.BackgroundProperty, "PreviewBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        Content = card;
        MouseLeftButtonUp += (_, _) => { onClick?.Invoke(); Close(); };
        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - ActualWidth - 4;
            Top = work.Bottom - ActualHeight - 4;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
                fade.Completed += (_, _) => Close();
                BeginAnimation(OpacityProperty, fade);
            };
            timer.Start();
        };
    }
}
