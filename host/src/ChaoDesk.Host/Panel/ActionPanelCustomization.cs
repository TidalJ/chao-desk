using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using ChaoDesk.Host.Desktop;
using ChaoDesk.Host.Localization;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Panel;

public sealed partial class ActionPanelWindow
{
    private void ResizeToPage()
    {
        double requestedHeight = _page == "home" ? _actions.HomeHeight : 510;
        if (!IsVisible) { Height = Math.Min(requestedHeight, _availableHeight); return; }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out RECT r)) { Height = Math.Min(requestedHeight, _availableHeight); return; }
        var placement = PanelPlacement.AtCursor(MonitorInfo.GetAll(), r.Left + (r.Right-r.Left)/2, r.Top + (r.Bottom-r.Top)/2, requestedHeight);
        _availableHeight = placement.AvailableHeightDip;
        Height = placement.HeightDip;
        var work = placement.Monitor.WorkArea;
        int h = placement.PixelBounds.Height, margin = placement.MarginPixels;
        int y = Math.Clamp(r.Top, work.Y + margin, Math.Max(work.Y + margin, work.Bottom - h - margin));
        SetWindowPos(hwnd, IntPtr.Zero, r.Left, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void BuildActions()
    {
        if (_actions.Items.Count == 0)
        {
            var hint = Label(Loc.Pick("Use settings to add actions.", "在设置中添加快捷操作。"));
            hint.Margin = new Thickness(3, 14, 3, 14);
            _body.Children.Add(hint);
            return;
        }
        var grid = new UniformGrid { Columns = PanelLayout.Columns };
        foreach (var item in _actions.Items)
        {
            var action = PanelAction.Find(item.ActionId);
            string customIcon = PanelActionPresentation.KindIcon(PanelActionPresentation.ResolvedKind(item));
            var button = ActionTile(action?.Icon ?? customIcon, PanelActionPresentation.ItemName(item), () => RunItem(item));
            button.DataContext = item;
            button.ToolTip = action == null ? item.Name + "\n" + (item.Kind is "text" or "script" or "command" ? PanelActionPresentation.KindTitle(item.Kind) : item.Target) : Loc.Pick(action.Title, action.Chinese);
            button.IsEnabled = item.ActionId != "clipboard" || _clipboardEnabled();
            grid.Children.Add(button);
        }
        _body.Children.Add(grid);
    }

    private Button ActionTile(string iconData, string name, Action run)
    {
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
        var icon = Icon(iconData, 23); icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 0, 8); content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.Normal, Foreground = Brushes.White, Effect = GlyphContrast(),
            TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        var button = Button(content, run);
        button.MinHeight = 76; button.Height = 76; button.Margin = new Thickness(3); button.Padding = new Thickness(4, 9, 4, 9);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        StyleAction(button); AutomationProperties.SetName(button, name);
        return button;
    }

    private bool _sendingCustomInput;

    private async void RunItem(PanelItem item)
    {
        if (item.ActionId.Length > 0) { Invoke(item.ActionId); return; }
        if (!PanelCustomActionRunner.TryCreatePlan(item,out var plan,out string error)) { _status.Text=PanelActionPresentation.Error(error); return; }
        bool input=plan.Shortcut!=null || plan.Text!=null;
        if(input && (_sendingCustomInput || !CanReturn())) {
            _status.Text=Loc.Pick("Open the panel over the window that should receive this action.","请在需要接收此操作的窗口上重新打开面板。"); return;
        }
        try
        {
            if(input) {
                _sendingCustomInput=true; _switching=true;
                IntPtr target=_previous;
                Hide(); SetForegroundWindow(target);
                await System.Threading.Tasks.Task.Delay(90);
                await ChaoDesk.Host.Input.KeyboardOutput.WaitForModifiersReleasedAsync();
                if(!IsWindow(target) || GetForegroundWindow()!=target)
                    throw new InvalidOperationException(Loc.Pick("The previous window could not receive input.","之前的窗口未能获得输入焦点。"));
                if(ChaoDesk.Host.Input.KeyboardOutput.HavePressedModifiers)
                    throw new InvalidOperationException(Loc.Pick("Release Ctrl, Alt, Shift and Win, then try again.","请松开 Ctrl、Alt、Shift 和 Win 后重试。"));
                if(plan.Shortcut is { } chord) ChaoDesk.Host.Input.KeyboardOutput.SendShortcutChecked(chord);
                else ChaoDesk.Host.Input.KeyboardOutput.TypeTextChecked(plan.Text!);
            }
            else { Hide(); await PanelCustomActionRunner.ExecuteAsync(plan); }
        }
        catch (Exception ex)
        {
            if(_closed) return;
            if(!IsVisible) Toggle();
            string detail=ex.Message.Trim();
            _status.Text = Loc.Pick("Action failed: ", "操作未完成：") + detail[..Math.Min(detail.Length,180)];
        }
        finally { if(input) { _switching=false; _sendingCustomInput=false; } }
    }

}
