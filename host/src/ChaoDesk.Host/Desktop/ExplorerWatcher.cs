using System.Windows.Threading;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Desktop;

/// <summary>
/// Detects Explorer restarts. Primary signal: the "TaskbarCreated" broadcast. Backup: a periodic
/// check that the parent window we attached to still exists (covers cases where the broadcast is
/// missed). Raises <see cref="ShellRestarted"/> about one second after the shell is back.
/// </summary>
public sealed class ExplorerWatcher : IDisposable
{
    private readonly MessageWindow _window;
    private readonly uint _taskbarCreated;
    private readonly DispatcherTimer _delay;
    private readonly DispatcherTimer _poll;
    private readonly Func<bool> _isTargetLost;

    public event Action? ShellRestarted;

    public ExplorerWatcher(MessageWindow window, Func<bool> isTargetLost)
    {
        _window = window;
        _isTargetLost = isTargetLost;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _window.Message += OnMessage;

        _delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _delay.Tick += (_, _) =>
        {
            _delay.Stop();
            if (FindWindow("Progman", null) == IntPtr.Zero)
            {
                // Shell still not ready; try again shortly.
                _delay.Start();
                return;
            }
            ShellRestarted?.Invoke();
        };

        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += (_, _) =>
        {
            if (!_delay.IsEnabled && _isTargetLost())
            {
                Log.Warning("Desktop parent window lost; scheduling re-attach");
                _delay.Start();
            }
        };
        _poll.Start();
    }

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            Log.Information("TaskbarCreated received (Explorer restarted)");
            _delay.Stop();
            _delay.Start();
        }
    }

    public void Dispose()
    {
        _window.Message -= OnMessage;
        _delay.Stop();
        _poll.Stop();
    }
}
