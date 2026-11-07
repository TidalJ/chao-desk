using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace ChaoDesk.Host;

/// <summary>
/// Independent of WPF and Serilog, so failures before normal logging are visible. A launch that starts fine (or a
/// helper process that ends cleanly) deletes its file again, so only launches that went wrong leave one behind.
/// </summary>
internal static class StartupDiagnostics
{
    private static readonly object Gate = new();
    private static readonly string FileName = $"startup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log";
    private static string _stage = "managed entry";
    private static Timer? _watchdog;
    private static int _samples;
    private static bool _done;   // started fine: the file is gone, only failures are written from now on
    private static bool _failed; // something went wrong: keep the file

    internal static void Begin()
    {
        Record($"Managed entry; exe={Environment.ProcessPath}; version={typeof(App).Assembly.GetName().Version}; " +
            $"base={AppContext.BaseDirectory}; cwd={Environment.CurrentDirectory}; " +
            $"session={Process.GetCurrentProcess().SessionId}; uptimeMs={Environment.TickCount64}; " +
            $"mode={Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() ?? "normal"}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Failure($"Unhandled exception; terminating={e.IsTerminating}; {e.ExceptionObject}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Record("ProcessExit");
            if (!_failed) Discard(); // a helper (--indexer, --exit, a second launch) that ended cleanly
        };
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
        Prune();
        _watchdog = new Timer(_ =>
        {
            Record($"Process alive; stage={Volatile.Read(ref _stage)}");
            if (Interlocked.Increment(ref _samples) >= 10) _watchdog?.Dispose();
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private const int KeptStartupLogs = 20;

    /// <summary>
    /// Every launch writes its own startup log; keep the newest few. Logs from before the app was renamed
    /// (lotuspond-*.log) are no longer written or read, so they go too.
    /// </summary>
    private static void Prune()
    {
        foreach (string folder in new[] { AppPaths.Logs, Path.Combine(Path.GetTempPath(), "ChaoDesk-startup") })
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                var old = new DirectoryInfo(folder).GetFiles("startup-*.log")
                    .Where(f => f.Name != FileName)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(KeptStartupLogs - 1)
                    .Concat(new DirectoryInfo(folder).GetFiles("lotuspond-*.log"));
                foreach (var file in old)
                {
                    try { file.Delete(); } catch { /* in use or gone: leave it */ }
                }
            }
            catch { /* diagnostics must not affect startup */ }
        }
    }

    internal static void Stage(string stage)
    {
        Volatile.Write(ref _stage, stage);
        Record(stage);
    }

    /// <summary>The app is up and its window thread answers: stop watching and remove this launch's file.</summary>
    internal static void Succeeded()
    {
        _watchdog?.Dispose();
        if (_failed) return;
        Discard();
        _done = true;
    }

    /// <summary>Something went wrong: written even after a good start, and the file is kept.</summary>
    internal static void Failure(string message)
    {
        _failed = true;
        _done = false;
        Record(message);
    }

    private static void Discard()
    {
        lock (Gate)
            foreach (string folder in new[] { AppPaths.Logs, Path.Combine(Path.GetTempPath(), "ChaoDesk-startup") })
                try { File.Delete(Path.Combine(folder, FileName)); } catch { /* diagnostics must not affect the app */ }
    }

    internal static void Record(string message)
    {
        if (_done) return; // routine notes after a good start belong in the normal log
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
