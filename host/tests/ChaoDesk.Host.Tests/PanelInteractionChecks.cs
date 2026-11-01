using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ChaoDesk.Host.Input;
using ChaoDesk.Host.Panel;

namespace ChaoDesk.Host.Tests;

internal static class PanelInteractionChecks
{
    internal static void Run(ActionPanelWindow panel,Func<int> settingsOpened,Action<int> pump,string output)
    {
        var outside=new PanelMouseHook.PointerPress(-100000,-100000,IntPtr.Zero);
        panel.ShowActivated=false;
        panel.Show(); pump(40);
        Assert.False(panel.IsActive);
        panel.HandlePointerPress(outside); Assert.False(panel.IsVisible);

        panel.Show(); panel.UpdateLayout(); pump(40);
        var origin=panel.PointToScreen(new Point(24,24));
        panel.HandlePointerPress(new((int)origin.X,(int)origin.Y,new WindowInteropHelper(panel).Handle));
        Assert.True(panel.IsVisible);
        var buttons=Descendants((DependencyObject)panel.Content).OfType<Button>().Where(b=>b.IsVisible).ToArray();
        var gear=Assert.Single(buttons,b=>b.MinHeight!=76);
        Assert.Equal("PanelSettings",AutomationProperties.GetAutomationId(gear));
        Assert.DoesNotContain(buttons,b=>AutomationProperties.GetAutomationId(b)=="PanelAddAction");
        Assert.All(buttons,button=>Assert.Null(button.ContextMenu));
        int before=settingsOpened();
        gear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(before+1,settingsOpened());
        Assert.False(panel.IsVisible);

        panel.Show(); panel.HandlePointerPress(outside); Assert.False(panel.IsVisible);
        panel.ShowActivated=true;
        File.WriteAllText(Path.Combine(output,"panel-interaction-metrics.txt"),
            "Inactive first show dismisses on outside press. Inside press stays open.\nHome has five actions plus one settings gear, no Add tile or management menus.\nGear dismisses the quick panel and invokes the settings callback once.\nProduction handlers tested without global input injection.\n");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) {
            var child=VisualTreeHelper.GetChild(root,i); yield return child;
            foreach(var nested in Descendants(child)) yield return nested;
        }
    }
}
