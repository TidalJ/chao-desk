using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ChaoDesk.Host.Localization;
using ChaoDesk.Host.Panel;
using ChaoDesk.Host.Settings;

namespace ChaoDesk.Host.Tests;

internal static class PanelSettingsEditorChecks
{
    internal static void Run(SettingsWindow window,ISettingsHost host,string output)
    {
        var layout=host.PanelActions;
        var editor=(PanelActionsEditor)((ContentControl)window.FindName("PanelActionsHost")).Content;
        T Find<T>(string id) where T:FrameworkElement => Descendants((DependencyObject)window.Content).OfType<T>()
            .Single(element=>AutomationProperties.GetAutomationId(element)==id);
        void Click(string id) { Find<Button>(id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout(); }
        void Set(string id,string text) => Find<TextBox>(id).Text=text;
        void Render(string name) => UiSnapshots.Render(window,Path.Combine(output,name+".png"));
        void Add(string kind) { Click("PanelManageAdd"); Click("AddKind-"+kind); }
        void Options() { foreach(var expander in Descendants((DependencyObject)window.Content).OfType<Expander>()) expander.IsExpanded=true; window.UpdateLayout(); }
        string file=Path.Combine(Path.GetTempPath(),"ChaoDesk-settings-editor-"+Guid.NewGuid().ToString("N")+".txt");
        File.WriteAllText(file,"Owned editor fixture; never opened.");
        try {
            foreach(string theme in new[] {"light","dark"}) {
                ThemeManager.Apply(theme); Render("panel-settings-"+theme);
            }
            ThemeManager.Apply("light");
            window.Width=760; window.Height=480; Render("panel-settings-small");
            window.Width=960; window.Height=640; window.UpdateLayout();
            string first=layout.Items[0].Id;
            Click("ManageDown-"+first); Assert.Equal(first,layout.Items[1].Id);
            Click("ManageUp-"+first); Assert.Equal(first,layout.Items[0].Id);
            var removed=layout.Items.Last(); Click("ManageRemove-"+removed.Id);
            Assert.DoesNotContain(layout.Items,item=>item.Id==removed.Id);
            Add("builtin"); Click("AddBuiltin-"+removed.ActionId);
            Assert.Equal(removed.ActionId,layout.Items.Last().ActionId);

            Click("PanelManageAdd"); Render("panel-settings-add");
            Assert.Equal(9,Descendants((DependencyObject)window.Content).OfType<Button>()
                .Count(button=>AutomationProperties.GetAutomationId(button).StartsWith("AddKind-")));
            Click("AddKind-program"); Options();
            Set("ActionName","App fixture"); Set("ActionTarget",file); Set("ActionArguments","--name \"two words\"");
            Set("ActionWorkingDirectory",Path.GetDirectoryName(file)!); Render("panel-settings-program"); Click("SavePanelAction");
            Assert.Equal("program",layout.Items.Last().Kind); Assert.Equal("--name \"two words\"",layout.Items.Last().Arguments);

            Add("file"); Set("ActionTarget",file); Render("panel-settings-file"); Click("SavePanelAction");
            var fileItem=layout.Items.Last(); Assert.Equal("file",fileItem.Kind);
            Click("ManageEdit-"+fileItem.Id); Set("ActionName","Document edited"); Click("SavePanelAction");
            Assert.Equal(fileItem.Id,layout.Items.Last().Id); Assert.Equal(file,layout.Items.Last().Target);

            Add("folder"); Set("ActionTarget",file); int before=layout.Items.Count;
            Click("SavePanelAction"); Assert.Equal(before,layout.Items.Count); Assert.Equal(file,Find<TextBox>("ActionTarget").Text);
            Set("ActionTarget",Path.GetDirectoryName(file)!); Click("SavePanelAction"); Assert.Equal("folder",layout.Items.Last().Kind);

            Add("url"); Set("ActionTarget","javascript:alert(1)"); before=layout.Items.Count;
            Click("SavePanelAction"); Assert.Equal(before,layout.Items.Count); Assert.Equal("javascript:alert(1)",Find<TextBox>("ActionTarget").Text);
            Set("ActionTarget","example.com/docs"); Click("SavePanelAction"); Assert.Equal("https://example.com/docs",layout.Items.Last().Target);

            Add("keys"); Set("ActionTarget","Ctrl+Shift+T");
            Click("RecordPanelKeys"); Assert.True(((UiSnapshots.FakeHost)host).HotkeysSuspended);
            editor.RefreshActions(); window.UpdateLayout();
            Assert.False(((UiSnapshots.FakeHost)host).HotkeysSuspended);
            Assert.Equal("Ctrl+Shift+T",Find<TextBox>("ActionTarget").Text);
            Click("SavePanelAction"); Assert.Equal("Ctrl+Shift+T",layout.Items.Last().Target);
            const string text="  你好\r\nsecond line  ";
            Add("text"); Set("ActionTarget",text); Render("panel-settings-text"); Click("SavePanelAction");
            var textItem=layout.Items.Last(); Click("ManageEdit-"+textItem.Id);
            Assert.Equal(text,Find<TextBox>("ActionTarget").Text); Set("ActionName","Greeting edited"); Click("SavePanelAction");
            Assert.Equal(textItem.Id,layout.Items.Last().Id); Assert.Equal(text,layout.Items.Last().Target);

            const string command="  echo fixture | findstr fixture && echo ready\r\n";
            Add("command"); Options(); Set("ActionTarget",command); Set("ActionWorkingDirectory",Path.GetDirectoryName(file)!);
            Render("panel-settings-command"); Click("SavePanelAction");
            var commandItem=layout.Items.Last(); Assert.Equal("cmd",commandItem.Engine); Assert.Equal(command,commandItem.Target);
            Click("ManageEdit-"+commandItem.Id); Assert.Equal(command,Find<TextBox>("ActionTarget").Text); Click("SavePanelAction");
            Assert.Equal(commandItem.Id,layout.Items.Last().Id);

            const string script="  Write-Output 'editor fixture'\r\n# Preserve space  ";
            Add("script"); Set("ActionTarget",script); Render("panel-settings-script");
            window.Width=760; window.Height=480; Render("panel-settings-script-small");
            window.Width=960; window.Height=640; window.UpdateLayout(); Click("SavePanelAction");
            Assert.Equal("powershell",layout.Items.Last().Engine); Assert.Equal(script,layout.Items.Last().Target);
            Assert.Equal(layout.Items.Select(item=>item.Id),host.Settings.PanelItems.Select(item=>item.Id));

            foreach(var action in PanelAction.All) { if(layout.IsFull) break; layout.AddAction(action.Id); }
            editor.RefreshActions(); Render("panel-settings-full"); Assert.False(Find<Button>("PanelManageAdd").IsEnabled);
            foreach(var item in layout.Items.ToArray()) layout.Remove(item.Id);
            editor.RefreshActions(); Render("panel-settings-empty"); Assert.True(Find<Button>("PanelManageAdd").IsEnabled);
            Assert.Empty(host.Settings.PanelItems);
            foreach(var item in PanelLayout.Defaults()) layout.AddAction(item.ActionId);
            editor.RefreshActions();
        }
        finally { File.Delete(file); ThemeManager.Apply("light"); Loc.Apply("zh"); }
        File.WriteAllText(Path.Combine(output,"panel-settings-editor-metrics.txt"),
            "Settings owns all nine Add choices; no saved action executed.\nEight custom kinds saved through real controls. Move/remove/builtin add/capacity/empty state checked.\nInvalid URL and file-as-folder preserve drafts; edits preserve IDs and whitespace; host settings stay synchronized.\nLight/dark and 760x480 layout renders use own controls, not desktop screenshots.\n");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) {
            var child=VisualTreeHelper.GetChild(root,i); yield return child;
            foreach(var nested in Descendants(child)) yield return nested;
        }
    }
}
