using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows.Threading;
using ChaoDesk.Host.Localization;
using Serilog;

namespace ChaoDesk.Host.Update;

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

/// <summary>
/// Checks GitHub for a newer release and installs it: downloads ChaoDeskSetup-*.exe to
/// %LOCALAPPDATA%\ChaoDesk\updates, checks its SHA-256 and runs it silently; the installer stops this app
/// (--exit) and starts the new one. Nothing is downloaded until the user chooses to install. Use on the UI thread.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string Repository = "TidalJ/desktop-wallpaper";
    private const string LatestUrl = $"https://github.com/{Repository}/releases/latest/download/latest.json";

    // Before Http: static initializers run in source order, and the client's User-Agent needs the version.
    public static Version Current { get; } = ReleaseFeed.Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();
    private readonly Action _requestExit;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _download;
    private Version? _notified;

    public UpdateService(Action requestExit)
    {
        _requestExit = requestExit;
        // The first automatic check waits until startup has settled, then one a day.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(24);
            if (State is UpdateState.Downloading or UpdateState.Installing) return;
            await CheckAsync();
            if (State == UpdateState.Available && Latest != null && Latest.Version != _notified)
            {
                _notified = Latest.Version;
                UpdateAvailable?.Invoke(Latest);
            }
        };
    }

    public UpdateState State { get; private set; }
    public ReleaseInfo? Latest { get; private set; }
    /// <summary>Why the last check or download failed, in the current interface language.</summary>
    public string? Error => _error?.Invoke();
    private Func<string>? _error;
    public double Progress { get; private set; }
    public DateTime? LastChecked { get; private set; }

    /// <summary>Raised whenever <see cref="State"/>, <see cref="Progress"/> or the release changes.</summary>
    public event Action? Changed;

    /// <summary>An automatic check found a newer version (once per version and session).</summary>
    public event Action<ReleaseInfo>? UpdateAvailable;

    /// <summary>Automatic daily checks on or off.</summary>
    public bool AutoCheck
    {
        get => _timer.IsEnabled;
        set
        {
            if (value == _timer.IsEnabled) return;
            if (value) _timer.Start(); else _timer.Stop();
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API refuses requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ChaoDesk/{Current}");
        return client;
    }

    public async Task CheckAsync()
    {
        if (State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing) return;
        Set(UpdateState.Checking);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await Http.SendAsync(request, timeout.Token);
            LastChecked = DateTime.Now;
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // No public release with a latest.json: none published yet, or the repository is private.
                // Never claim "latest".
                Log.Warning("Update check: GitHub has no public release manifest for {Repository}", Repository);
                Latest = null;
                Fail(() => Loc.T("No published release was found on GitHub."));
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Update check: GitHub answered {Status}", (int)response.StatusCode);
                int status = (int)response.StatusCode;
                string? reason = response.ReasonPhrase;
                Fail(() => Loc.F("GitHub answered {0} {1}. Try again later.", status, reason));
                return;
            }
            var release = ReleaseFeed.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (release == null)
            {
                Log.Warning("Update check: the release manifest is incomplete");
                Fail(() => Loc.T("The latest release's manifest could not be read."));
                return;
            }
            Latest = release;
            Log.Information("Update check: running {Current}, latest {Latest}", Current, release.Version);
            Set(release.Version > Current ? UpdateState.Available : UpdateState.UpToDate);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Warning(ex, "Update check failed");
            Fail(() => Loc.T("Could not reach GitHub. Check the connection and try again."));
        }
    }

    /// <summary>Downloads the newer installer, checks it and runs it; the app then exits.</summary>
    public async Task InstallAsync()
    {
        if (State != UpdateState.Available || Latest is not { } release) return;
        _download = new CancellationTokenSource();
        var token = _download.Token;
        Progress = 0;
        Set(UpdateState.Downloading);
        string folder = Path.Combine(AppPaths.DataRoot, "updates");
        string path = Path.Combine(folder, release.InstallerName), part = path + ".part";
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true); // older installers and broken downloads
            Directory.CreateDirectory(folder);

            string expected = release.Sha256;
            using (var response = await Http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? release.InstallerSize;
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(part);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    done += read;
                    if (total > 0 && (double)done / total - Progress >= 0.01)
                    {
                        Progress = Math.Min(1, (double)done / total);
                        Changed?.Invoke();
                    }
                }
            }
            string hash;
            await using (var check = File.OpenRead(part)) hash = Convert.ToHexString(await SHA256.HashDataAsync(check, token));
            if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The downloaded installer's SHA-256 {hash} does not match {expected}");
            File.Move(part, path, true);

            Log.Information("Installing Chao Desk {Version} from {Path}", release.Version, path);
            Set(UpdateState.Installing);
            // Per-user installer: no UAC prompt. It stops this app (--exit) and, with /RELAUNCH=1, starts the new one.
            Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = true });
            _requestExit();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            TryDelete(part);
            Set(UpdateState.Available);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update to {Version} failed", release.Version);
            TryDelete(part);
            bool damaged = ex is InvalidDataException;
            Fail(() => damaged ? Loc.T("The download was damaged. Try again.") : Loc.T("The download failed. Try again."));
        }
        finally
        {
            _download?.Dispose();
            _download = null;
        }
    }

    public void CancelDownload() => _download?.Cancel();

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Fail(Func<string> message)
    {
        _error = message;
        Set(UpdateState.Failed);
    }

    private void Set(UpdateState state)
    {
        if (state != UpdateState.Failed) _error = null;
        State = state;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _timer.Stop();
        _download?.Cancel();
    }
}
