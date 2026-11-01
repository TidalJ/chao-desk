using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace ChaoDesk.Host;

/// <summary>Independent of WPF and Serilog, so failures before normal logging are visible.</summary>
internal static class StartupDiagnostics
{
    private static readonly object Gate = new();
    private static readonly string FileName = $"startup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log";
    private static string _stage = "managed entry";
    private static Timer? _watchdog;
    private static int _samples;

    internal static void Begin()
    {
        Record($"Managed entry; exe={Environment.ProcessPath}; version={typeof(App).Assembly.GetName().Version}; " +
            $"base={AppContext.BaseDirectory}; cwd={Environment.CurrentDirectory}; " +
            $"session={Process.GetCurrentProcess().SessionId}; uptimeMs={Environment.TickCount64}; " +
            $"mode={Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() ?? "normal"}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Record($"Unhandled exception; terminating={e.IsTerminating}; {e.ExceptionObject}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Record("ProcessExit");
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            Record($"Elevated={new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)}");
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            using var approval = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            Record($"Run.ChaoDesk={run?.GetValue("ChaoDesk") ?? "<missing>"}; " +
                $"StartupApproved={(approval?.GetValue("ChaoDesk") is byte[] bytes ? Convert.ToHexString(bytes) : "<missing>")}");
        }
        catch (Exception ex) { Record($"Environment diagnostics failed: {ex}"); }
        _watchdog = new Timer(_ =>
        {
            Record($"Process alive; stage={Volatile.Read(ref _stage)}");
            if (Interlocked.Increment(ref _samples) >= 10) _watchdog?.Dispose();
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    internal static void Stage(string stage)
    {
        Volatile.Write(ref _stage, stage);
        Record(stage);
    }

    internal static void Record(string message)
    {
        // Never let diagnostic I/O prevent the app from starting. Use a fallback if the data folder is unavailable.
        string line = $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} tid={Environment.CurrentManagedThreadId} {message}{Environment.NewLine}";
        lock (Gate)
        {
            foreach (string folder in new[] { AppPaths.Logs, Path.Combine(Path.GetTempPath(), "ChaoDesk-startup") })
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    File.AppendAllText(Path.Combine(folder, FileName), line);
                    return;
                }
                catch { /* Try the fallback; diagnostics must not affect startup. */ }
            }
        }
    }
}
