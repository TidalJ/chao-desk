using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ChaoDesk.Host.Input;
using ChaoDesk.Host.Localization;
using ChaoDesk.Host.Panel;
using Microsoft.Win32;

namespace ChaoDesk.Host.Settings;

public sealed partial class PanelActionsEditor
{
    private TextBox? _name;
    private TextBox? _target;
    private TextBox? _arguments;
    private TextBox? _workingDirectory;
    private ComboBox? _engine;
    private TextBox? _recordTarget;
    private bool _recordWasReadOnly;
    private bool _hotkeysSuspended;

    private void ClearFields()
    { _name = null; _target = null; _arguments = null; _workingDirectory = null; _engine = null; }

    private void CaptureDraft()
    {
        if (_page != Page.Form || _draft == null || _target == null) return;
        _draft.Name = _name?.Text ?? _draft.Name;
        _draft.Target = _target.Text;
        _draft.Arguments = _arguments?.Text ?? _draft.Arguments;
        _draft.WorkingDirectory = _workingDirectory?.Text ?? _draft.WorkingDirectory;
        if (_engine != null) _draft.Engine = _engine.SelectedIndex switch { 0 => "powershell", 1 => "cmd", _ => "" };
        if (_draft.Kind == "command") _draft.Engine = "cmd";
    }

    private void BuildForm()
    {
        if (_draft == null) return;
        string kind = _draft.Kind;
        _header.Children.Add(Text(PanelActionPresentation.KindTitle(kind), title: true));
        _header.Children.Add(Hint(_editId == null ? Loc.T("Set up this action. Saving adds it to your panel.") : Loc.T("Update this action. Its position on your panel stays the same.")));
        _name = Field(Loc.T("Name (optional)"), _draft.Name, "ActionName");
        _name.MaxLength = 32;
        string targetLabel = kind switch
        {
            "program" => Loc.T("App or shortcut path"), "file" => Loc.T("File path"),
            "folder" => Loc.T("Folder path"), "url" => Loc.T("Website address"),
            "keys" => Loc.T("Key combination"), "text" => Loc.T("Text to send"),
            "command" => Loc.T("Command"), "script" => Loc.T("Script"), _ => Loc.T("Action content")
        };
        if (kind == "script")
        {
            AddLabel(Loc.T("Script engine"));
            _engine = new ComboBox { MinHeight = 32, Margin = new Thickness(0, 0, 0, 12) };
            _engine.Items.Add(Loc.T("PowerShell"));
            _engine.Items.Add(Loc.T("CMD / Batch"));
            _engine.SelectedIndex = _draft.Engine switch { "powershell" => 0, "cmd" => 1, _ => -1 };
            AutomationProperties.SetAutomationId(_engine, "ActionEngine");
            AutomationProperties.SetName(_engine, Loc.T("Script engine"));
            _body.Children.Add(_engine);
        }
        _target = Field(targetLabel, _draft.Target, "ActionTarget");
        if (kind is "text" or "script" or "command")
        {
            _target.AcceptsReturn = true;
            _target.AcceptsTab = true;
            _target.TextWrapping = TextWrapping.Wrap;
            _target.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _target.Height = kind == "command" ? 100 : 150;
            _target.VerticalContentAlignment = VerticalAlignment.Top;
            if (kind is "script" or "command")
                _target.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
        }
        if (kind is "program" or "file" or "folder" or "keys" or "script")
        {
            var actions = new WrapPanel { Margin = new Thickness(0, -4, 0, 12) };
            if (kind is "program" or "file")
                actions.Children.Add(Button(Loc.T("Choose file"), "BrowseActionFile", () => PickFile(_target!, kind == "program")));
            if (kind is "program" or "folder")
            {
                var folder = Button(Loc.T("Choose folder"), "BrowseActionFolder", () => PickFolder(_target!));
                folder.Margin = new Thickness(kind == "program" ? 6 : 0, 0, 0, 0);
                actions.Children.Add(folder);
            }
            if (kind == "keys")
                actions.Children.Add(Button(Loc.T("Record keys"), "RecordPanelKeys", () => StartRecording(_target!)));
            if (kind == "script")
                actions.Children.Add(Button(Loc.T("Load script file"), "LoadActionScript", LoadScript));
            _body.Children.Add(actions);
        }
        if (kind == "url") _body.Children.Add(Hint(Loc.T("Use an http or https address. An address without a scheme uses https.")));
        if (kind == "keys") _body.Children.Add(Hint(Loc.T("For example, Ctrl+Shift+T. Record keys, or type a combination. Esc cancels recording.")));
        if (kind == "text") _body.Children.Add(Hint(Loc.T("Line breaks and spaces are kept exactly as entered.")));
        if (kind == "command") _body.Children.Add(Hint(Loc.T("Runs with CMD. You can enter one command or several lines.")));
        if (kind == "program") _arguments = Field(Loc.T("Arguments (optional)"), _draft.Arguments, "ActionArguments");
        if (kind is "program" or "script" or "command")
        {
            _workingDirectory = Field(Loc.T("Working directory (optional)"), _draft.WorkingDirectory, "ActionWorkingDirectory");
            var browse = Button(Loc.T("Choose folder"), "BrowseActionWorkingDirectory", () => PickFolder(_workingDirectory!));
            browse.HorizontalAlignment = HorizontalAlignment.Left;
            browse.Margin = new Thickness(0, -4, 0, 12);
            _body.Children.Add(browse);
        }
        var save = Button(Loc.T("Save action"), "SavePanelAction", SaveAction);
        save.HorizontalAlignment = HorizontalAlignment.Right;
        save.Margin = new Thickness(0, 12, 8, 0);
        save.SetResourceReference(BackgroundProperty, "AccentSoftBrush");
        save.SetResourceReference(ForegroundProperty, "AccentBrush");
        _footer.Children.Add(save);
    }

    private void SaveAction()
    {
        StopRecording();
        CaptureDraft();
        if (_draft == null) return;
        if (_layout.SaveCustom(_editId, _draft.Name, _draft.Kind, _draft.Target, _draft.Arguments,
                _draft.WorkingDirectory, _draft.Engine, out string error)) Navigate(Page.List);
        else SetStatus(PanelActionPresentation.Error(error), true);
    }

    private TextBox Field(string label, string value, string id)
    {
        var labelBlock = AddLabel(label);
        var field = new TextBox
        {
            Text = value, MinHeight = 32, Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 0, 0, 12), VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(field, id);
        AutomationProperties.SetName(field, label);
        AutomationProperties.SetLabeledBy(field, labelBlock);
        _body.Children.Add(field);
        return field;
    }

    private TextBlock AddLabel(string label)
    {
        var block = Text(label);
        block.FontSize = 14;
        block.Margin = new Thickness(0, 0, 0, 5);
        _body.Children.Add(block);
        return block;
    }

    private void PickFile(TextBox target, bool program)
    {
        StopRecording();
        var picker = new OpenFileDialog
        {
            Title = Loc.T("Choose file"), CheckFileExists = true, DereferenceLinks = false,
            Filter = program ? Loc.T("Apps and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*") : Loc.T("All files (*.*)|*.*")
        };
        if (ShowDialog(picker) == true) { target.Text = picker.FileName; SetStatus(""); }
    }

    private void PickFolder(TextBox target)
    {
        StopRecording();
        var picker = new OpenFolderDialog { Title = Loc.T("Choose folder"), Multiselect = false };
        bool? selected = _owner == null ? picker.ShowDialog() : picker.ShowDialog(_owner);
        if (selected == true) { target.Text = picker.FolderName; SetStatus(""); }
    }

    private bool? ShowDialog(OpenFileDialog picker) => _owner == null ? picker.ShowDialog() : picker.ShowDialog(_owner);

    private void LoadScript()
    {
        StopRecording();
        var picker = new OpenFileDialog
        {
            Title = Loc.T("Load script file"), CheckFileExists = true,
            Filter = Loc.T("Script files (*.ps1;*.cmd;*.bat)|*.ps1;*.cmd;*.bat|All files (*.*)|*.*")
        };
        if (ShowDialog(picker) != true) return;
        try
        {
            string content = File.ReadAllText(picker.FileName);
            if (_target != null) _target.Text = content;
            string extension = Path.GetExtension(picker.FileName);
            if (_engine != null)
            {
                if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)) _engine.SelectedIndex = 0;
                else if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)) _engine.SelectedIndex = 1;
            }
            SetStatus("");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetStatus(Loc.T("Could not read this script file. Your draft has been kept."), true); }
    }

    private void StartRecording(TextBox target)
    {
        StopRecording();
        _recordTarget = target;
        _recordWasReadOnly = target.IsReadOnly;
        target.IsReadOnly = true;
        target.LostKeyboardFocus += RecordingLostFocus;
        _hotkeysSuspended = true;
        try { _suspendHotkeys?.Invoke(true); }
        catch { StopRecording(); throw; }
        target.Focus();
        SetStatus(Loc.T("Press a key combination. Esc cancels recording."));
    }

    private void RecordingLostFocus(object sender, KeyboardFocusChangedEventArgs e) => StopRecording();

    private void StopRecording()
    {
        bool wasRecording = _recordTarget != null;
        if (_recordTarget != null)
        {
            _recordTarget.LostKeyboardFocus -= RecordingLostFocus;
            _recordTarget.IsReadOnly = _recordWasReadOnly;
            _recordTarget = null;
        }
        if (_hotkeysSuspended)
        {
            _hotkeysSuspended = false;
            _suspendHotkeys?.Invoke(false);
        }
        if (wasRecording) SetStatus("");
    }

    private void RecordKey(object sender, KeyEventArgs e)
    {
        if (_recordTarget == null) return;
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (key == Key.Escape && modifiers == ModifierKeys.None) { StopRecording(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        string token = key switch
        {
            Key.Return => "Enter", Key.Back => "Backspace", Key.Prior => "PageUp", Key.Next => "PageDown",
            Key.Capital => "CapsLock", Key.OemPlus => "Plus", Key.OemMinus => "Minus", _ => key.ToString()
        };
        if (token.Length == 2 && token[0] == 'D' && char.IsAsciiDigit(token[1])) token = token[1..];
        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(token);
        TextBox target = _recordTarget;
        StopRecording();
        if (PanelKeySequence.TryParse(string.Join("+", parts), out var chord, out _)) target.Text = chord.DisplayText;
        else SetStatus(PanelActionPresentation.Error("InvalidKeys"), true);
    }
}
