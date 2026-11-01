using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Serilog;

namespace ChaoDesk.Host.Panel;

/// <summary>Local window-region sampling. One reusable DIB, one pending frame, no WGC session or disk output.</summary>
internal sealed class GlassBackdrop : IDisposable
{
    internal sealed record Frame(byte[] Pixels, int Width, int Height, int X, int Y) : IDisposable
    { public void Dispose() => ArrayPool<byte>.Shared.Return(Pixels); }
    private readonly IntPtr _window;
    private readonly Dispatcher _dispatcher;
    private readonly Func<Frame, bool> _deliver;
    private readonly Action<Exception> _failed;
    private readonly FrameQueue _frames;
    private readonly ManualResetEventSlim _stop = new();
    private Thread? _thread;
    private int _disposed;
    private int _retryUpload;
    private Rect _primedRegion = Rect.Empty;
    public int Delivered { get; private set; }
    private int _overscan = 160;
    public void SetOverscan(int pixels) => Volatile.Write(ref _overscan, Math.Clamp(pixels,160,1024));
    private volatile bool _moving;
    private readonly AutoResetEvent _wake = new(false);
    public void SetMoving(bool moving) { _moving = moving; try { _wake.Set(); } catch (ObjectDisposedException) { } }

    public GlassBackdrop(IntPtr window, Dispatcher dispatcher, Func<Frame, bool> deliver, Action<Exception> failed)
    {
        _window = window; _dispatcher = dispatcher; _deliver = deliver; _failed = failed;
        _frames = new FrameQueue(QueueDelivery, Deliver);
        // Earlier Windows accepts 0x11 as WDA_MONITOR and captures a black rectangle.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new NotSupportedException("Capture exclusion requires Windows 10 2004 or later");
        if (!SetWindowDisplayAffinity(window, 0x11)) throw new Win32Exception(Marshal.GetLastWin32Error());
        DwmFlush();
    }

    // Called while the popup is hidden, so its very first visible frame already has refracted content.
    public void Prime()
    {
        using var sampler = new Sampler();
        using var frame = sampler.Read(_window, true, Volatile.Read(ref _overscan))!;
        if (!_deliver(frame)) throw new InvalidOperationException("Initial glass texture was not ready");
        _primedRegion = new Rect(frame.X, frame.Y, frame.Width, frame.Height);
        Delivered++;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "ChaoDesk local glass" }; _thread.Start();
    }

    private void Run()
    {
        long samples = 0; double milliseconds = 0, captureMilliseconds = 0, compareMilliseconds = 0, copyMilliseconds = 0; int unchanged = 0;
        var lifetime = Stopwatch.StartNew();
        try
        {
            using var sampler = new Sampler(_primedRegion);
            using var pacer = new GlassFramePacer(_stop.WaitHandle, _wake);
            while (!_stop.IsSet)
            {
                long start = Stopwatch.GetTimestamp();
                var frame = sampler.Read(_window, force: Volatile.Read(ref _retryUpload) != 0, margin: Volatile.Read(ref _overscan));
                if (frame == null) unchanged++; else { unchanged = 0; _frames.Publish(frame); }
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                samples++; milliseconds += elapsed;
                captureMilliseconds += sampler.LastCaptureMilliseconds;
                compareMilliseconds += sampler.LastCompareMilliseconds;
                copyMilliseconds += sampler.LastCopyMilliseconds;
                if (samples == 120) Log.Information("Refractive glass CPU sample: {Cost:0.00} ms/sample, BitBlt+GdiFlush {Capture:0.00}, compare {Compare:0.00}, copies {Copy:0.00} ms/sample, {Frames} uploads; not GPU or present timing",
                    milliseconds / samples, captureMilliseconds / samples, compareMilliseconds / samples, copyMilliseconds / samples, Delivered);
                double wait = Math.Max(.1, ((!_moving && unchanged >= 8) ? 66 : 16.67) - elapsed);
                if (pacer.Wait(wait)) break;
            }
        }
        catch (Exception ex)
        {
            if (!_stop.IsSet && !_dispatcher.HasShutdownStarted)
                _dispatcher.BeginInvoke(() => { if (_disposed == 0) _failed(ex); });
        }
        finally
        {
            Log.Information("Refractive glass stopped: {Frames} uploads / {Seconds:0.0}s, CPU {Cost:0.00} ms/sample, BitBlt+GdiFlush {Capture:0.00}, compare {Compare:0.00}, copies {Copy:0.00} ms/sample; not GPU or present timing",
                Delivered, lifetime.Elapsed.TotalSeconds, milliseconds / Math.Max(1, samples), captureMilliseconds / Math.Max(1, samples),
                compareMilliseconds / Math.Max(1, samples), copyMilliseconds / Math.Max(1, samples));
            _stop.Dispose();
            _wake.Dispose();
        }
    }

    private bool QueueDelivery(Action callback)
    {
        if (_dispatcher.HasShutdownStarted) return false;
        return _dispatcher.BeginInvoke(callback, DispatcherPriority.Render).Status != DispatcherOperationStatus.Aborted;
    }

    private void Deliver(Frame frame)
    {
        if (_deliver(frame)) { Volatile.Write(ref _retryUpload, 0); Delivered++; }
        // A busy render buffer must not stall the native move loop. Recapture
        // even unchanged pixels next tick so the last changed frame is not lost.
        else Volatile.Write(ref _retryUpload, 1);
    }

    // Native move/size loops can delay Dispatcher Render work. Present only the
    // already captured mailbox frame on that same UI thread; never capture here.
    public void PresentLatestDuringMove() => _frames.PresentLatest();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // The UI never waits for a driver call or joins the capture worker.
        try { _stop.Set(); } catch (ObjectDisposedException) { }
        _frames.Dispose();
        SetWindowDisplayAffinity(_window, 0);
        if (_thread == null) { _stop.Dispose(); _wake.Dispose(); }
    }

    // Publish may run on the capture worker; presentation runs on the UI thread.
    // Scheduling is separate so native movement can consume frames without
    // pretending the pending Render callback has run.
    internal sealed class FrameQueue(Func<Action, bool> queueRender, Action<Frame> deliver) : IDisposable
    {
        private Frame? _latest;
        private int _queued, _disposed;

        internal void Publish(Frame frame)
        {
            if (Volatile.Read(ref _disposed) != 0) { frame.Dispose(); return; }
            Interlocked.Exchange(ref _latest, frame)?.Dispose();
            if (Volatile.Read(ref _disposed) != 0) { Interlocked.Exchange(ref _latest, null)?.Dispose(); return; }
            if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0) return;
            try
            {
                if (!queueRender(DrainQueued)) Dispose();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void DrainQueued()
        {
            // Only execution of the scheduled callback releases its queue slot.
            Interlocked.Exchange(ref _queued, 0);
            PresentLatest();
        }

        internal void PresentLatest()
        {
            using var frame = Interlocked.Exchange(ref _latest, null);
            if (Volatile.Read(ref _disposed) != 0 || frame == null) return;
            deliver(frame);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Exchange(ref _latest, null)?.Dispose();
        }
    }

    internal sealed class Sampler : IDisposable
    {
        private IntPtr _screen, _memory, _bitmap, _oldBitmap, _bits;
        private int _width, _height;
        private byte[]? _previous;
        private bool _previousValid;
        private RECT _last;
        private Rect _region;
        internal double LastCaptureMilliseconds { get; private set; }
        internal double LastCompareMilliseconds { get; private set; }
        internal double LastCopyMilliseconds { get; private set; }
        public Sampler(Rect? initialRegion = null)
        {
            _region = initialRegion ?? Rect.Empty;
            _screen = GetDC(IntPtr.Zero);
            if (_screen != IntPtr.Zero) _memory = CreateCompatibleDC(_screen);
            if (_memory == IntPtr.Zero) { Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        }
        public unsafe Frame? Read(IntPtr window, bool force = false, int margin = 0)
        {
            LastCaptureMilliseconds = LastCompareMilliseconds = LastCopyMilliseconds = 0;
            if (!GetWindowRect(window, out RECT r)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (margin > 0) {
                int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);
                int vw=GetSystemMetrics(78), vh=GetSystemMetrics(79);
                _region = GlassCaptureRegion.Select(new Rect(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top),
                    new Rect(vx,vy,vw,vh), margin, _region);
                r.Left=(int)_region.Left; r.Top=(int)_region.Top;
                r.Right=(int)_region.Right; r.Bottom=(int)_region.Bottom;
            }
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 1 || h < 1 || w > 4096 || h > 4096) throw new InvalidOperationException("Invalid glass surface bounds");
            if (w != _width || h != _height)
            {
                FreeBitmap();
                var info = new BitmapInfo { Size = 40, Width = w, Height = -h, Planes = 1, Bits = 32 };
                _bitmap = CreateDIBSection(_screen, ref info, 0, out _bits, IntPtr.Zero, 0);
                if (_bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                _oldBitmap = SelectObject(_memory, _bitmap); _width = w; _height = h; _previous = null; _previousValid=false;
            }
            long captureStart = Stopwatch.GetTimestamp();
            if (!BitBlt(_memory, 0, 0, w, h, _screen, r.Left, r.Top, 0x40CC0020)) throw new Win32Exception(Marshal.GetLastWin32Error());
            GdiFlush();
            LastCaptureMilliseconds = Stopwatch.GetElapsedTime(captureStart).TotalMilliseconds;
            // The captured rectangle is screen-anchored. Motion does not invalidate it:
            // the renderer reprojects its UVs to the current HWND position.
            var pixels = new ReadOnlySpan<byte>((void*)_bits, w * h * 4);
            bool stationary=_last.Equals(r);
            long compareStart = Stopwatch.GetTimestamp();
            bool unchanged = !force && _previousValid && stationary && pixels.SequenceEqual(_previous);
            LastCompareMilliseconds = Stopwatch.GetElapsedTime(compareStart).TotalMilliseconds;
            if (unchanged) return null;
            long copyStart = Stopwatch.GetTimestamp();
            _previous ??= new byte[pixels.Length];
            // No duplicate full-frame copy while the capture rectangle is moving.
            if(stationary) pixels.CopyTo(_previous);
            _previousValid=stationary; _last = r;
            var copy = ArrayPool<byte>.Shared.Rent(pixels.Length); pixels.CopyTo(copy);
            LastCopyMilliseconds = Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds;
            return new Frame(copy, w, h, r.Left, r.Top);
        }
        private void FreeBitmap()
        {
            if (_oldBitmap != IntPtr.Zero && _oldBitmap != new IntPtr(-1)) SelectObject(_memory, _oldBitmap);
            if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
            _bitmap = _oldBitmap = _bits = IntPtr.Zero;
        }
        public void Dispose()
        {
            FreeBitmap();
            if (_memory != IntPtr.Zero) DeleteDC(_memory);
            if (_screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screen);
            _screen = _memory = IntPtr.Zero;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int X, Y; public uint Used, Important, Color; }
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr source, int sx, int sy, uint operation);
}
