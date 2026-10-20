using System.IO;
using System.Text.Json;
using ChaoDesk.Host.Desktop;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace ChaoDesk.Host.Rendering;

/// <summary>
/// Owns the shared WebView2 environment and one <see cref="WallpaperWindow"/> per monitor.
/// Rebuilds everything when monitors change or Explorer restarts. UI thread only.
/// </summary>
public sealed class WallpaperManager : IDisposable
{
    /// <summary>
    /// Chromium flags that trim background work.
    /// </summary>
    public const string BrowserArguments =
        "--disable-background-networking --disable-component-update --disable-sync --disable-translate " +
        "--disable-default-apps --disable-extensions --no-first-run --disable-client-side-phishing-detection " +
        "--disable-features=Translate,OptimizationHints,MediaRouter,AutofillServerCommunication,SpareRendererForSitePerProcess " +
        "--autoplay-policy=no-user-gesture-required";

    private readonly List<WallpaperWindow> _windows = new();
    private readonly Dictionary<string, bool> _policyPaused = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _devTools;
    private CoreWebView2Environment? _env;
    private WallpaperContent? _content;
    private bool _userPaused;
    private bool _rebuilding;
    private bool _rebuildPending;
    private bool _disposed;

    public WallpaperManager(bool devTools)
    {
        _devTools = devTools;
    }

    /// <summary>Produces the JSON object injected as <c>window.__LOTUS_INIT__</c> for a given monitor.</summary>
    public Func<WallpaperContent, MonitorInfo, bool, string> InitJsonFactory { get; set; } =
        (_, _, paused) => JsonSerializer.Serialize(new { paused });

    /// <summary>Restricts which monitors get a wallpaper window (null = all).</summary>
    public Func<MonitorInfo, bool>? MonitorFilter { get; set; }

    public IReadOnlyList<WallpaperWindow> Windows => _windows;
    public DesktopTarget? Target { get; private set; }
    public WallpaperContent? Content => _content;
    public bool IsUserPaused => _userPaused;
    public string? BrowserVersion => _env?.BrowserVersionString;

    /// <summary>Raised after a rebuild or any pause-state change.</summary>
    public event Action? StateChanged;

    /// <summary>Raised when any page posts a message.</summary>
    public event Action<WallpaperWindow, JsonElement>? PageMessage;

    public async Task StartAsync(WallpaperContent content)
    {
        _content = content;
        if (_env == null)
        {
            Directory.CreateDirectory(AppPaths.WebView2UserData);
            var options = new CoreWebView2EnvironmentOptions(BrowserArguments);
            _env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebView2UserData, options);
            Log.Information("WebView2 environment created (runtime {Version})", _env.BrowserVersionString);
        }
        await RebuildAsync("startup");
    }

    /// <summary>Removes every wallpaper window (the wallpaper was turned off). <see cref="StartAsync"/> brings it back.</summary>
    public void Stop()
    {
        DisposeWindows();
        _content = null;
        Target = null;
        StateChanged?.Invoke();
    }

    /// <summary>Tear down and recreate all wallpaper windows. Calls made during a rebuild are coalesced.</summary>
    public async Task RebuildAsync(string reason)
    {
        if (_disposed || _env == null || _content == null) return;
        if (_rebuilding) { _rebuildPending = true; return; }
        _rebuilding = true;
        try
        {
            do
            {
                _rebuildPending = false;
                await RebuildCoreAsync(reason);
                reason = "coalesced";
            } while (_rebuildPending && !_disposed);
        }
        finally
        {
            _rebuilding = false;
        }
        StateChanged?.Invoke();
    }

    private async Task RebuildCoreAsync(string reason)
    {
        Log.Information("Rebuilding wallpaper windows ({Reason})", reason);
        DisposeWindows();

        var target = DesktopAttacher.FindTarget();
        Target = target;
        Log.Information("Desktop target: {Target}", target);
        if (target.Progman != IntPtr.Zero)
            Log.Information("Progman children (Z-order): {Children}", DesktopAttacher.DescribeProgmanChildren(target.Progman));
        if (!target.IsValid)
        {
            Log.Error("No valid desktop parent window; wallpaper not shown");
            return;
        }

        var monitors = MonitorInfo.GetAll().Where(m => MonitorFilter?.Invoke(m) ?? true).ToList();
        foreach (var m in monitors) Log.Information("Monitor: {Monitor}", m);

        foreach (var monitor in monitors)
        {
            var w = WallpaperWindow.Create(monitor);
            w.MessageReceived += OnWindowMessage;
            w.DestroyedExternally += OnWindowDestroyedExternally;
            _windows.Add(w);
            if (!w.AttachTo(target)) continue;
            await w.InitializeAsync(_env!, _devTools);
            if (_disposed) return;
            bool paused = EffectivePaused(monitor);
            await w.NavigateAsync(_content!, InitJsonFactory(_content!, monitor, paused));
            if (paused) w.SetPaused(true);
        }
    }

    /// <summary>Switch every window to new content without recreating the windows.</summary>
    public async Task SetContentAsync(WallpaperContent content)
    {
        _content = content;
        foreach (var w in _windows.ToList())
        {
            bool paused = EffectivePaused(w.Monitor);
            await w.NavigateAsync(content, InitJsonFactory(content, w.Monitor, paused));
            if (paused) w.SetPaused(true);
        }
        StateChanged?.Invoke();
    }

    /// <summary>Reload the current content in every window (e.g. after the manifest changed on disk).</summary>
    public Task ReloadAsync() => _content == null ? Task.CompletedTask : SetContentAsync(_content);

    public void SetUserPaused(bool paused)
    {
        _userPaused = paused;
        ApplyPause();
    }

    /// <summary>Update automatic pause decisions, keyed by monitor device name.</summary>
    public void SetPolicyPaused(IReadOnlyDictionary<string, bool> pausedByDevice)
    {
        _policyPaused.Clear();
        foreach (var kv in pausedByDevice) _policyPaused[kv.Key] = kv.Value;
        ApplyPause();
    }

    public bool EffectivePaused(MonitorInfo m) =>
        _userPaused || (_policyPaused.TryGetValue(m.DeviceName, out bool p) && p);

    private void ApplyPause()
    {
        bool changed = false;
        foreach (var w in _windows)
        {
            bool paused = EffectivePaused(w.Monitor);
            if (paused != w.IsPaused) { w.SetPaused(paused); changed = true; }
        }
        if (changed) StateChanged?.Invoke();
    }

    public void Broadcast(string json)
    {
        foreach (var w in _windows) w.Post(json);
    }

    /// <summary>The window whose monitor contains the physical screen point, or null.</summary>
    public WallpaperWindow? WindowAt(int x, int y)
    {
        foreach (var w in _windows)
            if (w.Hwnd != IntPtr.Zero && w.Monitor.Bounds.Contains(x, y)) return w;
        return null;
    }

    public int RunningCount => _windows.Count(w => w.Hwnd != IntPtr.Zero && !w.IsPaused);
    public int WindowCount => _windows.Count(w => w.Hwnd != IntPtr.Zero);

    /// <summary>True if the parent window we attached to no longer exists.</summary>
    public bool IsTargetLost => Target != null && _windows.Count > 0 && !Target.IsValid;

    private void OnWindowMessage(WallpaperWindow w, JsonElement msg)
    {
        if (msg.TryGetProperty("type", out var t) && t.GetString() == "ready")
        {
            Log.Information("Page ready on {Monitor}: {Message}", w.Monitor.DeviceName, msg.GetRawText());
            // The page may have missed a pause sent before its listener existed; re-send the current state.
            if (w.IsPaused) w.Post("""{"type":"pause"}""");
        }
        else if (msg.TryGetProperty("type", out t) && t.GetString() == "log")
        {
            string level = msg.TryGetProperty("level", out var l) ? l.GetString() ?? "info" : "info";
            string text = msg.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            if (level == "error") Log.Error("[page {Monitor}] {Text}", w.Monitor.DeviceName, text);
            else if (level == "warn") Log.Warning("[page {Monitor}] {Text}", w.Monitor.DeviceName, text);
            else Log.Information("[page {Monitor}] {Text}", w.Monitor.DeviceName, text);
        }
        PageMessage?.Invoke(w, msg);
    }

    private void OnWindowDestroyedExternally(WallpaperWindow w)
    {
        // Typically Explorer died and took its WorkerW (and our child windows) with it.
        // ExplorerWatcher triggers the rebuild once the shell is back.
        StateChanged?.Invoke();
    }

    private void DisposeWindows()
    {
        foreach (var w in _windows)
        {
            w.MessageReceived -= OnWindowMessage;
            w.DestroyedExternally -= OnWindowDestroyedExternally;
            w.Dispose();
        }
        _windows.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeWindows();
    }
}
