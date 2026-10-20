using Microsoft.Win32;
using Serilog;

namespace ChaoDesk.Host.Settings;

/// <summary>"Start with Windows" via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "ChaoDesk";

    public static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string s && s.Length > 0;
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
            else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
            Log.Information("Start with Windows: {Enabled}", enabled);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not update the Run registry key");
        }
    }
}
