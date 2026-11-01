using System.Threading;
using Serilog;

namespace ChaoDesk.Host;

/// <summary>
/// Named-mutex single instance. A second launch signals the running instance instead of starting:
/// no arguments → open settings; <c>--exit</c> → the running instance shuts down cleanly (used by the
/// uninstaller and scripts).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\ChaoDesk.SingleInstance";
    private const string ShowEventName = @"Local\ChaoDesk.ShowSettings";
    private const string ExitEventName = @"Local\ChaoDesk.Exit";
    private const string PanelEventName = @"Local\ChaoDesk.Panel";
    private readonly EventWaitHandle? _panelEvent;
    private readonly RegisteredWaitHandle? _panelWait;

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _showEvent;
    private readonly EventWaitHandle? _exitEvent;
    private readonly RegisteredWaitHandle? _showWait;
    private readonly RegisteredWaitHandle? _exitWait;

    public bool IsPrimary { get; }

    public SingleInstance(Action onShowRequested, Action onExitRequested, Action? onPanelRequested = null)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
        IsPrimary = created;
        if (!IsPrimary) return;

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => onShowRequested(), null, Timeout.Infinite, false);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => onExitRequested(), null, Timeout.Infinite, false);
        _panelEvent = new EventWaitHandle(false, EventResetMode.AutoReset, PanelEventName);
        _panelWait = ThreadPool.RegisterWaitForSingleObject(_panelEvent, (_, _) => onPanelRequested?.Invoke(), null, Timeout.Infinite, false);
    }

    /// <summary>Signal the running instance. Returns false if no instance is running.</summary>
    public static bool SignalRunningInstance(bool exit, bool panel = false)
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(exit ? ExitEventName : panel ? PanelEventName : ShowEventName);
            ev.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not signal running instance");
            return false;
        }
    }

    public void Dispose()
    {
        _showWait?.Unregister(null);
        _panelWait?.Unregister(null);
        _panelEvent?.Dispose();
        _exitWait?.Unregister(null);
        _showEvent?.Dispose();
        _exitEvent?.Dispose();
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned on this thread */ }
        }
        _mutex.Dispose();
    }
}
