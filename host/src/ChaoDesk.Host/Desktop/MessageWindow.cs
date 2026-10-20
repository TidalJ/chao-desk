using System.Windows.Interop;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Desktop;

/// <summary>
/// Hidden top-level window (on the UI thread) that receives shell / power / session broadcasts.
/// It must be top-level rather than message-only: broadcasts such as "TaskbarCreated" are not
/// delivered to HWND_MESSAGE windows.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private readonly HwndSource _source;

    public delegate void MessageHandler(uint msg, IntPtr wParam, IntPtr lParam, ref bool handled);

    public event MessageHandler? Message;

    public IntPtr Handle => _source.Handle;

    /// <summary>Run <paramref name="action"/> on the thread that owns this window.</summary>
    public void Dispatch(Action action) => _source.Dispatcher.BeginInvoke(action);

    public MessageWindow()
    {
        var p = new HwndSourceParameters("ChaoDesk.MessageWindow")
        {
            WindowStyle = unchecked((int)WS_POPUP),
            ExtendedWindowStyle = (int)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE),
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
        };
        _source = new HwndSource(p);
        _source.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        Message?.Invoke((uint)msg, wParam, lParam, ref handled);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(Hook);
        _source.Dispose();
    }
}
