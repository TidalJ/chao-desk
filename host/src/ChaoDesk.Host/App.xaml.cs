using System.Windows;
using System.Windows.Threading;
using ChaoDesk.Host.Desktop;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace ChaoDesk.Host;

public partial class App : Application
{
#if DEBUG
    public const bool IsDebugBuild = true;
#else
    public const bool IsDebugBuild = false;
#endif

    private AppHost? _host;
    private SingleInstance? _singleInstance;
    private bool _cleanedUp;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (RunHelperMode(e.Args)) return;
        bool exitRequested = e.Args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase));

        _singleInstance = new SingleInstance(
            onShowRequested: () => Dispatcher.BeginInvoke(ShowSettings),
            onExitRequested: () => Dispatcher.BeginInvoke(ExitApp));
        if (!_singleInstance.IsPrimary || exitRequested)
        {
            // Another instance owns the desktop: forward the request and quit.
            if (!_singleInstance.IsPrimary) SingleInstance.SignalRunningInstance(exitRequested);
            _singleInstance.Dispose();
            _singleInstance = null;
            _cleanedUp = true;
            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();
        Logging.Initialize();
        Log.Information("Chao Desk {Version} starting (OS {Os})", typeof(App).Assembly.GetName().Version, Environment.OSVersion);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            Log.Fatal(ev.ExceptionObject as Exception, "Unhandled exception");
            Cleanup();
        };
        SessionEnding += (_, _) => Cleanup();

        try
        {
            _host = new AppHost(IsDebugBuild, ExitApp);
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to start");
            MessageBox.Show(Localization.Loc.F("Chao Desk could not start:\n\n{0}", ex.Message), "Chao Desk",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApp();
        }
    }

    private void ShowSettings() => _host?.ShowSettings();

    /// <summary>
    /// The same executable doubles as the elevated file index helper and its one-time setup.
    /// Those modes run without the single-instance check, a tray icon or any window.
    /// </summary>
    private bool RunHelperMode(string[] args)
    {
        if (args.Length == 0) return false;
        string mode = args[0].ToLowerInvariant();
        if (mode is not ("--indexer" or "--register-indexer" or "--unregister-indexer")) return false;
        System.IO.Directory.CreateDirectory(AppPaths.Logs);
        Logging.Initialize("indexer");
        int code;
        try
        {
            code = mode switch
            {
                "--indexer" => Files.IndexerHelper.Run(),
                "--register-indexer" => args.Length > 1 ? Files.IndexerTask.RegisterElevated(args[1]) : 1,
                _ => Files.IndexerTask.UnregisterElevated(),
            };
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "{Mode} failed", mode);
            code = 1;
        }
        Log.CloseAndFlush();
        _cleanedUp = true;
        Shutdown(code);
        return true;
    }

    /// <summary>The wallpaper needs the WebView2 Runtime; offer the download page when it is missing.</summary>
    internal static bool CheckWebView2Runtime()
    {
        try
        {
            string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            Log.Information("WebView2 Runtime {Version} found", version);
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Log.Error("WebView2 Runtime not installed");
            var r = MessageBox.Show(
                Localization.Loc.T("The wallpaper needs the Microsoft Edge WebView2 Runtime, which is not installed. The launcher and clipboard work without it.\n\nOpen the download page now?") +
                "\n\nhttps://developer.microsoft.com/microsoft-edge/webview2/",
                "Chao Desk", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
            }
            return false;
        }
    }

    private void ExitApp()
    {
        Cleanup();
        Shutdown();
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        Log.Information("Shutting down");
        try { _host?.Dispose(); } catch (Exception ex) { Log.Warning(ex, "Host dispose failed"); }
        DesktopAttacher.RefreshDesktop();
        _singleInstance?.Dispose();
        Log.CloseAndFlush();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true; // keep the wallpaper alive; the error is logged
    }
}
