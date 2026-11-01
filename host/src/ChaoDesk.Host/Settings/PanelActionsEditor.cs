using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ChaoDesk.Host.Localization;
using ChaoDesk.Host.Panel;
using DrawingPath = System.Windows.Shapes.Path;

namespace ChaoDesk.Host.Settings;

/// <summary>Edits the shared quick-panel layout without dispatching any action.</summary>
public sealed partial class PanelActionsEditor : UserControl
{
    private enum Page { List, Kinds, Builtins, Form }
    private readonly PanelLayout _layout;
    private readonly Action<bool>? _suspendHotkeys;
    private readonly Grid _root = new();
    private readonly StackPanel _header = new();
    private readonly StackPanel _body = new();
    private readonly StackPanel _footer = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _status;
    private Page _page;
    private string? _editId;
    private PanelItem? _draft;
    private string _message = "";
    private bool _messageIsError;
    private Window? _owner;

    public PanelActionsEditor(PanelLayout layout, Action<bool>? suspendHotkeys = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _suspendHotkeys = suspendHotkeys;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
        FontSize = 13;
        SetResourceReference(ForegroundProperty, "InkBrush");
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.Children.Add(_header);
        _scroll = new ScrollViewer
        {
            Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0)
        };
        Grid.SetRow(_scroll, 1);
        _root.Children.Add(_scroll);
        Grid.SetRow(_footer, 2);
        _root.Children.Add(_footer);
        _status = Text("", muted: true);
        _status.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetAutomationId(_status, "PanelManageStatus");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Content = _root;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopRecording(); };
        PreviewKeyDown += RecordKey;
        RenderPage();
    }

    /// <summary>Refreshes saved rows while retaining an open form and its unsaved values.</summary>
    public void RefreshActions()
    {
        CaptureDraft();
        StopRecording();
        RenderPage();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loc.Changed -= RefreshActions;
        Loc.Changed += RefreshActions;
        if (_owner != null) _owner.Deactivated -= OwnerDeactivated;
        _owner = Window.GetWindow(this);
        if (_owner != null) _owner.Deactivated += OwnerDeactivated;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopRecording();
        Loc.Changed -= RefreshActions;
        if (_owner != null) _owner.Deactivated -= OwnerDeactivated;
        _owner = null;
    }

    private void OwnerDeactivated(object? sender, EventArgs e) => StopRecording();

    private void Navigate(Page page)
    {
        StopRecording();
        _message = "";
        _page = page;
        if (page != Page.Form) { _draft = null; _editId = null; }
        RenderPage();
        _scroll.ScrollToTop();
    }

    private void RenderPage()
    {
        ClearFields();
        _header.Children.Clear();
        _body.Children.Clear();
        _footer.Children.Clear();
        if (_page == Page.List) BuildList();
        else
        {
            var back = Button(Loc.T("Back"), "PanelManageBack", () =>
                Navigate(_page == Page.Form && _editId == null || _page == Page.Builtins ? Page.Kinds : Page.List));
            back.HorizontalAlignment = HorizontalAlignment.Left;
            back.Margin = new Thickness(0, 0, 0, 10);
            _header.Children.Add(back);
            if (_page == Page.Kinds) BuildKinds();
            else if (_page == Page.Builtins) BuildBuiltins();
            else BuildForm();
        }
        _footer.Children.Add(_status);
        UpdateStatus();
    }

    private void BuildList()
    {
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = true };
        var add = Button(Loc.T("Add action"), "PanelManageAdd", () => Navigate(Page.Kinds));
        add.IsEnabled = !_layout.IsFull;
        DockPanel.SetDock(add, Dock.Right);
        top.Children.Add(add);
        var count = Text(Loc.F("{0} of {1} actions", _layout.Items.Count, PanelLayout.Capacity), muted: true);
        count.FontSize = 12.5;
        count.Margin = new Thickness(4, 0, 8, 0);
        count.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(count);
        _header.Children.Add(top);
        if (_layout.IsFull)
            _header.Children.Add(Hint(Loc.T("Remove an action to make room for another.")));
        if (_layout.Items.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(0, 24, 0, 24) };
            empty.Children.Add(Text(Loc.T("Your panel starts here"), title: true));
            empty.Children.Add(Hint(Loc.T("Add an app, a file or a tool you use often. Arrange actions in the order you want them on the panel.")));
            _body.Children.Add(empty);
            return;
        }
        var rows = new StackPanel();
        var card = new Border { Child = rows };
        card.SetResourceReference(StyleProperty, "Card");
        _body.Children.Add(card);
        for (int index = 0; index < _layout.Items.Count; index++)
        {
            PanelItem item = _layout.Items[index];
            int position = index;
            var row = new Grid { Margin = new Thickness(0, 11, 0, 11), MinHeight = 36 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = Icon(PanelActionPresentation.ItemIcon(item));
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(icon);
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var name = Text("", wrap: false);
            name.FontSize = 14;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            // User names are data, not localization keys.
            name.SetBinding(TextBlock.TextProperty, new Binding { Source = PanelActionPresentation.ItemName(item) });
            name.ToolTip = PanelActionPresentation.ItemName(item);
            labels.Children.Add(name);
            var kind = Text(PanelActionPresentation.KindTitle(item.ActionId.Length > 0 ? "builtin" : PanelActionPresentation.ResolvedKind(item)), muted: true, wrap: false);
            kind.FontSize = 12.5;
            kind.Margin = new Thickness(0, 3, 0, 0);
            kind.TextTrimming = TextTrimming.CharacterEllipsis;
            labels.Children.Add(kind);
            Grid.SetColumn(labels, 1);
            row.Children.Add(labels);
            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var edit = IconButton("M4,16 L3,21 L8,20 L20,8 L16,4 Z M14,6 L18,10", Loc.T("Edit"), "ManageEdit-" + item.Id, () => Edit(item));
            edit.IsEnabled = item.ActionId.Length == 0;
            edit.Visibility = edit.IsEnabled ? Visibility.Visible : Visibility.Hidden;
            controls.Children.Add(edit);
            var up = IconButton("M6,15 L12,9 L18,15", Loc.T("Move up"), "ManageUp-" + item.Id, () => { _layout.Move(item.Id, position - 1); RefreshActions(); });
            up.IsEnabled = position > 0;
            controls.Children.Add(up);
            var down = IconButton("M6,9 L12,15 L18,9", Loc.T("Move down"), "ManageDown-" + item.Id, () => { _layout.Move(item.Id, position + 1); RefreshActions(); });
            down.IsEnabled = position < _layout.Items.Count - 1;
            controls.Children.Add(down);
            controls.Children.Add(IconButton("M5,7 L19,7 M9,7 L9,4 L15,4 L15,7 M7,7 L8,21 L16,21 L17,7 M10,10 L10,18 M14,10 L14,18", Loc.T("Remove"), "ManageRemove-" + item.Id, () => { _layout.Remove(item.Id); RefreshActions(); }));
            Grid.SetColumn(controls, 2);
            row.Children.Add(controls);
            var separator = new Border { BorderThickness = new Thickness(0, 0, 0, index + 1 < _layout.Items.Count ? 1 : 0), Child = row };
            separator.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            rows.Children.Add(separator);
        }
    }

    private void BuildKinds()
    {
        _header.Children.Add(Text(Loc.T("Choose an action type"), title: true));
        _header.Children.Add(Hint(Loc.T("Configure it here, then use it from your quick panel.")));
        foreach (string kind in new[] { "program", "file", "folder", "url", "keys", "text", "command", "script", "builtin" })
        {
            var button = Choice(PanelActionPresentation.KindIcon(kind), PanelActionPresentation.KindTitle(kind), "AddKind-" + kind, () =>
            {
                if (kind == "builtin") { Navigate(Page.Builtins); return; }
                _editId = null;
                _draft = new PanelItem { Kind = kind, Engine = kind == "command" ? "cmd" : "powershell" };
                _page = Page.Form;
                _message = "";
                RenderPage();
                _scroll.ScrollToTop();
            });
            button.IsEnabled = !_layout.IsFull;
            _body.Children.Add(button);
        }
    }

    private void BuildBuiltins()
    {
        _header.Children.Add(Text(PanelActionPresentation.KindTitle("builtin"), title: true));
        _header.Children.Add(Hint(Loc.T("Choose a tool to add to your panel.")));
        foreach (PanelAction action in PanelAction.All)
        {
            bool added = _layout.Items.Any(item => item.ActionId == action.Id);
            var button = Choice(action.Icon, Loc.Pick(action.Title, action.Chinese), "AddBuiltin-" + action.Id, () =>
            {
                if (_layout.AddAction(action.Id)) Navigate(Page.List);
                else SetStatus(PanelActionPresentation.Error(_layout.IsFull ? "PanelFull" : "InvalidTarget"), true);
            });
            button.IsEnabled = !added && !_layout.IsFull;
            if (added) button.ToolTip = Loc.T("Already on your panel");
            _body.Children.Add(button);
        }
    }

    private void Edit(PanelItem item)
    {
        StopRecording();
        _editId = item.Id;
        _draft = new PanelItem
        {
            Id = item.Id, Kind = PanelActionPresentation.ResolvedKind(item), Name = item.Name,
            Target = item.Target, Arguments = item.Arguments, WorkingDirectory = item.WorkingDirectory, Engine = item.Engine
        };
        _page = Page.Form;
        _message = "";
        RenderPage();
        _scroll.ScrollToTop();
    }

    private void SetStatus(string message, bool error = false)
    { _message = message; _messageIsError = error; UpdateStatus(); }

    private void UpdateStatus()
    {
        _status.Text = _message;
        _status.Visibility = _message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _status.SetResourceReference(TextBlock.ForegroundProperty, _messageIsError ? "DangerBrush" : "MutedBrush");
    }

    private static TextBlock Text(string text, bool muted = false, bool title = false, bool wrap = true)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = title ? 16 : 13, FontWeight = title ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "InkBrush");
        return block;
    }

    private static TextBlock Hint(string text)
    {
        var block = Text(text, muted: true);
        block.FontSize = 12.5;
        block.Margin = new Thickness(0, 5, 0, 12);
        return block;
    }

    private static Button Button(string label, string id, Action click)
    {
        var button = new Button { Content = label, MinHeight = 32, Padding = new Thickness(12, 5, 12, 5) };
        button.SetResourceReference(StyleProperty, "SecondaryButton");
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, label);
        button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1 : .4;
        button.Click += (_, _) => click();
        return button;
    }

    private static Viewbox Icon(string geometry, double size = 20)
    {
        var icon = new DrawingPath
        {
            Data = Geometry.Parse(geometry), Width = 24, Height = 24, Stretch = Stretch.None,
            StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "InkBrush");
        // Preserve the shared 24-unit drawing space, including each glyph's optical margins.
        return new Viewbox
        {
            Width = size, Height = size, Stretch = Stretch.Uniform, Child = icon,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Button IconButton(string geometry, string label, string id, Action click)
    {
        var button = Button(label, id, click);
        button.Content = Icon(geometry, 18);
        button.ToolTip = label;
        button.Width = 32;
        button.Height = 32;
        button.Padding = new Thickness(6);
        button.Margin = new Thickness(4, 0, 0, 0);
        return button;
    }

    private static Button Choice(string geometry, string label, string id, Action click)
    {
        var button = Button(label, id, click);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Margin = new Thickness(0, 0, 0, 7);
        button.Padding = new Thickness(12, 9, 12, 9);
        var row = new DockPanel();
        var icon = Icon(geometry);
        icon.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var labelBlock = Text(label);
        labelBlock.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(labelBlock);
        button.Content = row;
        return button;
    }
}
