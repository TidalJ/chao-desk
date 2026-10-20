using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Files;

/// <summary>
/// <c>ChaoDesk.exe --indexer</c>: the elevated helper, started through the "File index" scheduled task. It connects
/// to the app's pipe (refusing anything but our own executable on the other end), streams every NTFS volume's
/// names and then their changes, and exits as soon as the app goes away.
/// </summary>
public static class IndexerHelper
{
    public static int Run()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Log.Error("The file index helper must run elevated");
            return 2;
        }
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* not important */ }

        using var pipe = new NamedPipeClientStream(".", IndexProtocol.PipeName(Process.GetCurrentProcess().SessionId), PipeDirection.Out);
        try { pipe.Connect(15_000); }
        catch (TimeoutException)
        {
            Log.Warning("The app's file index pipe did not answer");
            return 3;
        }
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) ||
            !string.Equals(GetProcessPath(serverPid), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
        {
            Log.Error("Refusing to index for an unknown process ({Pid})", serverPid);
            return 4;
        }

        var output = new IndexWriter(pipe);
        try
        {
            output.Hello();
            // Where the app's saved index left each journal (data only, validated against the live journal).
            var checkpoints = IndexCheckpoints.Read(IndexCheckpoints.DefaultPath);
            var running = new HashSet<char>();
            Log.Information("File index helper started");

            // Deliver changes promptly, notice quickly when the app has exited (the next write fails), and pick
            // up drives that are plugged in later.
            for (int tick = 0; ; tick++)
            {
                if (tick % 40 == 0) StartNewDrives(output, checkpoints, running);
                Thread.Sleep(250);
                output.Ping(); // also tells the app to apply what it has buffered
                output.Flush();
            }
        }
        catch (IOException)
        {
            Log.Information("The app closed the file index pipe; helper exiting");
            return 0;
        }
    }

    /// <summary>Starts a reader for each NTFS drive that has none yet (at start, and every ten seconds after).</summary>
    private static void StartNewDrives(IndexWriter output, IReadOnlyDictionary<char, IndexCheckpoints.Entry> checkpoints, HashSet<char> running)
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.Name.Length < 2 || !char.IsAsciiLetter(d.Name[0])) continue;
            char letter = char.ToUpperInvariant(d.Name[0]);
            lock (running) if (running.Contains(letter)) continue;
            try { if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady || d.DriveFormat != "NTFS") continue; }
            catch (IOException) { continue; }
            lock (running) running.Add(letter);
            var reader = new UsnVolume(letter, output, checkpoints.TryGetValue(letter, out var cp) ? cp : null);
            Log.Information("Indexing {Drive}", d.Name);
            new Thread(() =>
            {
                try { reader.Run(); }
                catch (IOException) { Environment.Exit(0); } // the app closed the pipe
                catch (Exception ex) { Log.Error(ex, "Indexing {Drive} failed", d.Name); }
                finally { lock (running) running.Remove(letter); }
            }) { IsBackground = true, Name = $"ChaoDesk.Usn.{letter}" }.Start();
        }
    }
}
