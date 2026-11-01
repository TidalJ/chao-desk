using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using ChaoDesk.Host.Input;
using Serilog;

namespace ChaoDesk.Host.Panel;

internal sealed record PanelCustomActionPlan(PanelItem Action, ProcessStartInfo? StartInfo,
    KeyboardChord? Shortcut = null, string? Text = null,
    string? TemporaryScriptPath = null, string? TemporaryScriptContent = null);

/// <summary>Plans are inert. Only an explicit ExecuteAsync call starts a custom process.</summary>
internal static class PanelCustomActionRunner
{
    private const string ScriptEnvironmentVariable = "CHAODESK_ACTION_SCRIPT";
    private const string PowerShellBootstrap = "& ([ScriptBlock]::Create([System.IO.File]::ReadAllText($env:CHAODESK_ACTION_SCRIPT, [System.Text.Encoding]::UTF8)))";
    private const int CapturedOutputLimit = 4096;

    internal static bool TryCreatePlan(PanelItem item, [NotNullWhen(true)] out PanelCustomActionPlan? plan, out string error)
    {
        plan = null;
        if (!string.IsNullOrEmpty(item.ActionId))
        { error = "Built-in actions are executed by the panel."; return false; }
        if (!PanelCustomAction.TryNormalize(item, out var action, out error)) return false;
        if (action.Kind == "keys")
        {
            if (!PanelKeySequence.TryParse(action.Target, out var chord, out error)) return false;
            plan = new(action, null, Shortcut: chord);
            return true;
        }
        if (action.Kind == "text")
        {
            plan = new(action, null, Text: action.Target);
            return true;
        }

        if (action.Kind is not ("script" or "command"))
        {
            // Pass the filename and the user's argument field directly to the
            // program/association. There is no cmd wrapper and no runas verb.
            bool executable = action.Kind == "program" && Path.GetExtension(action.Target).Equals(".exe", StringComparison.OrdinalIgnoreCase);
            var launch = new ProcessStartInfo(action.Target)
            {
                UseShellExecute = !executable,
                Arguments = action.Kind == "program" ? action.Arguments : "",
                WorkingDirectory = action.WorkingDirectory,
            };
            plan = new(action, launch);
            return true;
        }

        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = action.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChaoDesk", "PanelScripts", "action-" + Guid.NewGuid().ToString("N") +
            (action.Engine == "powershell" ? ".ps1" : ".cmd"));
        start.Environment[ScriptEnvironmentVariable] = path;
        if (action.Engine == "powershell")
        {
            start.FileName = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
            // Only this fixed loader is encoded. The user's UTF-8 body and its
            // path never enter the command line, whose length Windows limits.
            // ScriptBlock invocation preserves command-body execution without
            // changing the machine's execution policy or loading a profile.
            foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(PowerShellBootstrap)) }) start.ArgumentList.Add(argument);
            plan = new(action, start, TemporaryScriptPath: path, TemporaryScriptContent: action.Target);
        }
        else
        {
            start.FileName = Path.Combine(system, "cmd.exe");
            // cmd does not implement the usual argv quoting rules. Its command
            // string is fixed; the absolute file path is supplied as data in
            // the child environment and expanded once inside double quotes.
            // /d disables AutoRun and /v:off preserves literal ! in file paths.
            start.Arguments = "/d /s /q /v:off /c \"\"%CHAODESK_ACTION_SCRIPT%\"\"";
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            plan = new(action, start, TemporaryScriptPath: path,
                TemporaryScriptContent: "@echo off\r\nchcp 65001 >nul\r\n" + action.Target);
        }
        error = "";
        return true;
    }

    internal static Task ExecuteAsync(PanelItem item, CancellationToken cancellationToken = default)
    {
        if (!TryCreatePlan(item, out var plan, out string error)) throw new ArgumentException(error, nameof(item));
        return ExecuteAsync(plan, cancellationToken);
    }

    internal static async Task ExecuteAsync(PanelCustomActionPlan plan, CancellationToken cancellationToken = default)
    {
        if (plan.StartInfo is not { } start)
            throw new InvalidOperationException("Keyboard and text actions must be sent to the panel's restored target window.");
        cancellationToken.ThrowIfCancellationRequested();
        CheckLaunchPaths(plan.Action);
        if (plan.Action.Kind == "program" && Directory.Exists(plan.Action.Target)) start.UseShellExecute = true;
        bool scriptCreated = false;
        try
        {
            if (plan.TemporaryScriptPath is { } path)
            {
                string directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                // Never replace an existing file, even in the unlikely event
                // of a random filename collision.
                await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    scriptCreated = true;
                    byte[] bytes = new UTF8Encoding(false).GetBytes(plan.TemporaryScriptContent!);
                    await file.WriteAsync(bytes, cancellationToken);
                }
            }

            using Process? process = Process.Start(start);
            // Shell associations can open an existing app and return no new
            // process handle. That is a successful launch, not an error.
            if (plan.Action.Kind is not ("script" or "command")) return;
            if (process == null) throw new InvalidOperationException("Windows did not start the script interpreter.");
            using var drainCancellation = new CancellationTokenSource();
            Task<string> output = ReadBoundedOutputAsync(process.StandardOutput, drainCancellation.Token);
            Task<string> errors = ReadBoundedOutputAsync(process.StandardError, drainCancellation.Token);
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                catch (InvalidOperationException) { }
                finally { drainCancellation.Cancel(); }
                await Task.WhenAll(output, errors);
                throw;
            }
            // A script can launch a GUI child which inherits its output pipe.
            // Once the interpreter exits, give final output time to drain but
            // do not keep the action pending until that unrelated app closes.
            drainCancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
            string[] captured = await Task.WhenAll(output, errors);
            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(captured[1]) ? captured[0] : captured[1];
                throw new InvalidOperationException($"Script exited with code {process.ExitCode}." +
                    (string.IsNullOrWhiteSpace(detail) ? "" : "\n" + detail.Trim()));
            }
        }
        finally
        {
            if (scriptCreated && plan.TemporaryScriptPath is { } path) DeleteScript(path);
        }
    }

    private static void CheckLaunchPaths(PanelItem action)
    {
        if (action.WorkingDirectory.Length > 0 && !Directory.Exists(action.WorkingDirectory))
            throw new DirectoryNotFoundException("The selected working directory no longer exists.");
        if (action.Kind == "file" && !File.Exists(action.Target))
            throw new FileNotFoundException("The selected file or folder no longer exists.", action.Target);
        if (action.Kind == "folder" && !Directory.Exists(action.Target))
            throw new DirectoryNotFoundException("The selected file or folder no longer exists.");
        if (action.Kind == "program" && !File.Exists(action.Target) && !Directory.Exists(action.Target))
            throw new FileNotFoundException("The selected program or shortcut no longer exists.", action.Target);
        if (action.Kind == "shortcut" && !action.Target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !action.Target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !File.Exists(action.Target) && !Directory.Exists(action.Target))
            throw new FileNotFoundException("The selected file or folder no longer exists.", action.Target);
    }

    private static async Task<string> ReadBoundedOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                int remaining = CapturedOutputLimit - result.Length;
                if (remaining > 0) result.Append(buffer, 0, Math.Min(count, remaining));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        return result.ToString();
    }

    private static void DeleteScript(string path)
    {
        try { File.Delete(path); }
        catch (IOException ex) { Log.Debug(ex, "Could not remove completed panel script"); }
        catch (UnauthorizedAccessException ex) { Log.Debug(ex, "Could not remove completed panel script"); }
    }
}
