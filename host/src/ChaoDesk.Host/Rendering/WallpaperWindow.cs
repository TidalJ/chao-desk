using System.Runtime.InteropServices;
using System.Text.Json;
using ChaoDesk.Host.Desktop;
using Microsoft.Web.WebView2.Core;
using Serilog;
using static ChaoDesk.Host.Desktop.NativeMethods;

namespace ChaoDesk.Host.Rendering;

/// <summary>What a wallpaper window displays: a folder mapped to https://wallpaper.lotus/ plus its entry page.</summary>
public sealed record WallpaperContent(string Id, string Folder, string Entry, System.Drawing.Color Background);

/// <summary>
/// One per monitor: a plain Win32 child window hosting a WebView2 controller.
/// We use a raw window + <see cref="CoreWebView2Controller"/> rather than the WPF WebView2 control so we
/// control bounds and rasterization scale in physical pixels exactly.
/// Must be used from the UI thread.
/// </summary>
public sealed class WallpaperWindow : IDisposable
{
    public const string VirtualHost = "wallpaper.lotus";

    /// <summary>Serves only the user's Windows wallpaper picture (<see cref="DesktopImagePath"/>), for the widgets-only page.</summary>
    public const string DesktopHost = "desktop.lotus";

    /// <summary>The picture behind https://desktop.lotus/ (null: none). Set by the app while the widgets-only page shows.</summary>
    public static string? DesktopImagePath { get; set; }
    private const string ClassName = "ChaoDeskWallpaperWindow";

    private static readonly Dictionary<IntPtr, WallpaperWindow> Instances = new();
    private static readonly WndProc WndProcDelegate = StaticWndProc; // keep alive for the process lifetime
    private static bool _classRegistered;

    private CoreWebView2Controller? _controller;
    private string? _initScriptId;
    private bool _disposed;

    public IntPtr Hwnd { get; private set; }
    public MonitorInfo Monitor { get; private set; }
    public WallpaperContent? Content { get; private set; }
    public CoreWebView2? WebView => _controller?.CoreWebView2;

    /// <summary>True once the page has posted its <c>ready</c> message for the current navigation.</summary>
    public bool IsPageReady { get; private set; }

    /// <summary>Last pause state sent to the page.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Raised when the page posts a message (already parsed JSON).</summary>
    public event Action<WallpaperWindow, JsonElement>? MessageReceived;

    /// <summary>Raised when the native window is destroyed from outside (e.g. Explorer restarted).</summary>
    public event Action<WallpaperWindow>? DestroyedExternally;

    private WallpaperWindow(MonitorInfo monitor)
    {
        Monitor = monitor;
    }

    public static WallpaperWindow Create(MonitorInfo monitor)
    {
        EnsureClassRegistered();
        var w = new WallpaperWindow(monitor);
        var b = monitor.Bounds;
        w.Hwnd = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ClassName, "Chao Desk Wallpaper",
            WS_POPUP | WS_CLIPCHILDREN, b.X, b.Y, b.Width, b.Height, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (w.Hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
        Instances[w.Hwnd] = w;
        return w;
    }

    public bool AttachTo(DesktopTarget target)
    {
        bool ok = DesktopAttacher.Attach(Hwnd, target, Monitor.Bounds);
        Log.Information("Wallpaper window 0x{Hwnd:X} attached={Ok} for {Monitor}", Hwnd, ok, Monitor);
        return ok;
    }

    public async Task InitializeAsync(CoreWebView2Environment env, bool devToolsEnabled)
    {
        _controller = await env.CreateCoreWebView2ControllerAsync(Hwnd);
        if (_disposed) { _controller.Close(); _controller = null; return; }

        _controller.ShouldDetectMonitorScaleChanges = false;
        ApplyBounds();
        _controller.IsVisible = true;

        var s = _controller.CoreWebView2.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsStatusBarEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.AreDevToolsEnabled = devToolsEnabled;
        s.IsSwipeNavigationEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = true;

        var core = _controller.CoreWebView2;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += (_, e) =>
            Log.Debug("Navigation completed on {Monitor}: success={Success} status={Status} url={Url}",
                Monitor.DeviceName, e.IsSuccess, e.WebErrorStatus, core.Source);
        core.ProcessFailed += (_, e) => Log.Error("WebView2 process failed: {Kind} ({Reason}) on {Monitor}", e.ProcessFailedKind, e.Reason, Monitor.DeviceName);

        // Enforce "no network": only requests to the virtual host are allowed.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
    }

    /// <summary>Load a wallpaper. <paramref name="initJson"/> becomes <c>window.__LOTUS_INIT__</c> before any page script runs.</summary>
    public async Task NavigateAsync(WallpaperContent content, string initJson)
    {
        var core = WebView;
        if (core == null) return;
        Content = content;
        IsPageReady = false;
        IsPaused = false;

        _controller!.DefaultBackgroundColor = content.Background;
        core.ClearVirtualHostNameToFolderMapping(VirtualHost);
        core.SetVirtualHostNameToFolderMapping(VirtualHost, content.Folder, CoreWebView2HostResourceAccessKind.Allow);

        if (_initScriptId != null) core.RemoveScriptToExecuteOnDocumentCreated(_initScriptId);
        _initScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.__LOTUS_INIT__ = {initJson};");

        // Files under the virtual host can be served from the HTTP cache, so an updated wallpaper might still
        // run old scripts. Drop only the disk cache (never localStorage: the pond saves its koi there).
        try { await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache); }
        catch (Exception ex) { Log.Debug(ex, "Could not clear the WebView2 disk cache"); }

        ApplyMemoryTarget();
        string url = $"https://{VirtualHost}/{content.Entry.TrimStart('/')}";
        Log.Information("Navigating {Monitor} to {Url} ({Folder})", Monitor.DeviceName, url, content.Folder);
        core.Navigate(url);
    }

    public void Post(string json)
    {
        try { WebView?.PostWebMessageAsJson(json); }
        catch (Exception ex) { Log.Debug(ex, "PostWebMessageAsJson failed"); }
    }

    public void SetPaused(bool paused)
    {
        if (paused == IsPaused) return;
        IsPaused = paused;
        Post(paused ? """{"type":"pause"}""" : """{"type":"resume"}""");
        ApplyMemoryTarget();
    }

    /// <summary>
    /// A paused pond, and the widgets-only page (nothing animates), ask WebView2 to hold as little memory as it can:
    /// it trims caches and lets Windows page out what is idle, at the cost of a slower first frame when needed again.
    /// </summary>
    private void ApplyMemoryTarget()
    {
        var core = WebView;
        if (core == null) return;
        bool low = IsPaused || Content?.Entry == "widgets.html";
        try { core.MemoryUsageTargetLevel = low ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal; }
        catch (Exception ex) { Log.Debug(ex, "MemoryUsageTargetLevel not supported"); }
    }

    public void OpenDevTools() => WebView?.OpenDevToolsWindow();

    public Task<bool> CapturePreviewAsync(string pngPath) => CapturePreviewCoreAsync(pngPath);

    private async Task<bool> CapturePreviewCoreAsync(string pngPath)
    {
        var core = WebView;
        if (core == null) return false;
        try
        {
            await using var fs = System.IO.File.Create(pngPath);
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Preview capture failed");
            return false;
        }
    }

    public string? BrowserVersion => WebView?.Environment.BrowserVersionString;

    private void ApplyBounds()
    {
        if (_controller == null) return;
        _controller.RasterizationScale = Monitor.Scale;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, Monitor.Bounds.Width, Monitor.Bounds.Height);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !uri.Host.Equals(VirtualHost, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("Blocked navigation to {Uri}", e.Uri);
            e.Cancel = true;
            return;
        }
        IsPageReady = false;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) &&
            (uri.Host.Equals(VirtualHost, StringComparison.OrdinalIgnoreCase) || uri.Scheme is "data" or "blob"))
            return;
        if (uri != null && uri.Host.Equals(DesktopHost, StringComparison.OrdinalIgnoreCase) && DesktopImagePath is { } picture)
        {
            try
            {
                var stream = new System.IO.FileStream(picture, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
                e.Response = WebView!.Environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {DesktopBackground.ContentType(picture)}\r\nCache-Control: no-store");
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Log.Information(ex, "Could not read the Windows wallpaper {Path}", picture);
                e.Response = WebView!.Environment.CreateWebResourceResponse(null, 404, "Not found", "");
            }
            return;
        }
        Log.Warning("Blocked network request to {Uri}", e.Request.Uri);
        e.Response = WebView!.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var t) && t.GetString() == "ready")
                IsPageReady = true;
            MessageReceived?.Invoke(this, root.Clone());
        }
        catch (JsonException ex)
        {
            Log.Debug(ex, "Ignoring malformed web message");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _controller?.Close(); } catch (Exception ex) { Log.Debug(ex, "Controller close failed"); }
        _controller = null;
        if (Hwnd != IntPtr.Zero)
        {
            Instances.Remove(Hwnd);
            if (IsWindow(Hwnd)) DestroyWindow(Hwnd);
            Hwnd = IntPtr.Zero;
        }
    }

    private static void EnsureClassRegistered()
    {
        if (_classRegistered) return;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        if (RegisterClassEx(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()})");
        _classRegistered = true;
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return new IntPtr(1); // WebView2 paints everything; avoid a flash.
            case WM_MOUSEACTIVATE:
                return new IntPtr(MA_NOACTIVATE);
            case WM_DESTROY:
                if (Instances.TryGetValue(hwnd, out var w) && !w._disposed)
                {
                    Instances.Remove(hwnd);
                    w.Hwnd = IntPtr.Zero;
                    Log.Warning("Wallpaper window for {Monitor} was destroyed externally", w.Monitor.DeviceName);
                    w.DestroyedExternally?.Invoke(w);
                }
                break;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
