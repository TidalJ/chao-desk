using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;

namespace ChaoDesk.Host.Files;

/// <summary>
/// The "\Chao Desk\File index" scheduled task: runs <c>ChaoDesk.exe --indexer</c> with highest privileges in the
/// user's session. Creating it needs one UAC prompt; afterwards the app starts it on demand without any prompt
/// (the same trick Everything's service plays).
/// </summary>
public static class IndexerTask
{
    public const string TaskName = @"\Chao Desk\File index";

    public enum TaskState { Missing, Ready, WrongPath }

    /// <summary>Is the task registered, and does it start this executable?</summary>
    public static TaskState Query()
    {
        var (code, output) = RunSchtasks($"/Query /TN \"{TaskName}\" /XML");
        if (code != 0) return TaskState.Missing;
        var m = Regex.Match(output, @"<Command>(.*?)</Command>", RegexOptions.Singleline);
        string command = m.Success ? SecurityElement.FromString($"<x>{m.Groups[1].Value}</x>")?.Text ?? m.Groups[1].Value : "";
        return string.Equals(command.Trim('"'), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ? TaskState.Ready : TaskState.WrongPath;
    }

    /// <summary>Starts the helper. Returns false if the task could not be started.</summary>
    public static bool Start() => RunSchtasks($"/Run /TN \"{TaskName}\"").Code == 0;

    /// <summary>Runs this executable elevated with <paramref name="arguments"/> (one UAC prompt). False if the user declined.</summary>
    public static async Task<bool> RunElevatedAsync(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = true, Verb = "runas" });
            if (p == null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: the user said no
        {
            return false;
        }
    }

    public static string RegisterArguments() => $"--register-indexer {WindowsIdentity.GetCurrent().User?.Value}";
    public const string UnregisterArguments = "--unregister-indexer";

    /// <summary><c>--register-indexer &lt;sid&gt;</c>, running elevated: create or replace the task for that user.</summary>
    public static int RegisterElevated(string userSid)
    {
        if (!Regex.IsMatch(userSid, @"^S-1-[0-9-]+$")) return 1;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Chao Desk file search: reads the NTFS file tables so the launcher can find any file instantly. Started by Chao Desk when it runs; exits when Chao Desk exits.</Description>
              </RegistrationInfo>
              <Principals>
                <Principal id="Author">
                  <UserId>{userSid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(Environment.ProcessPath)}</Command>
                  <Arguments>--indexer</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), $"ChaoDesk-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            var (code, output) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            if (code != 0) Log.Error("schtasks /Create failed ({Code}): {Output}", code, output.Trim());
            else Log.Information("Registered the file index task for {Sid}", userSid);
            return code;
        }
        finally { try { File.Delete(file); } catch { /* temp file */ } }
    }

    /// <summary><c>--unregister-indexer</c>, running elevated.</summary>
    public static int UnregisterElevated()
    {
        var (code, output) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        if (code != 0) Log.Warning("schtasks /Delete failed ({Code}): {Output}", code, output.Trim());
        return code;
    }

    private static (int Code, string Output) RunSchtasks(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return (-1, "");
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15_000);
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "schtasks {Arguments} failed", arguments);
            return (-1, "");
        }
    }
}
