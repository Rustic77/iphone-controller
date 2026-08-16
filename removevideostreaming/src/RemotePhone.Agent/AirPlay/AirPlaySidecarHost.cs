using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RemotePhone.Agent.Core.AirPlay;

namespace RemotePhone_Agent.AirPlay;

/// <summary>
/// Downloads (once) and runs the GPLv3 AirPlay-Windows receiver as a sidecar process.
/// </summary>
public sealed class AirPlaySidecarHost : IAsyncDisposable
{
    private readonly AirPlaySidecarSpec _spec;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private Process? _process;

    public AirPlaySidecarHost(AirPlaySidecarSpec spec, ILogger logger, HttpClient? httpClient = null)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttp = false;
        }
    }

    public bool IsRunning => _process is { HasExited: false };

    public int? ProcessId => IsRunning ? _process!.Id : null;

    public async Task<string> EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (AirPlaySidecarArchive.IsInstalled(_spec))
        {
            _logger.LogInformation("AirPlay sidecar already installed at {Path}", _spec.ExePath);
            return _spec.ExePath;
        }

        _logger.LogInformation("Downloading AirPlay sidecar from {Url}", _spec.DownloadUrl);
        var bytes = await _http.GetByteArrayAsync(new Uri(_spec.DownloadUrl), cancellationToken)
            .ConfigureAwait(false);
        var exe = AirPlaySidecarArchive.Extract(bytes, _spec);
        _logger.LogInformation("AirPlay sidecar extracted to {Path} ({Bytes} bytes zip)", exe, bytes.Length);
        return exe;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var exe = await EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);
        if (IsRunning)
        {
            return;
        }

        var start = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = _spec.InstallDirectory,
            Arguments = _spec.Arguments,
            UseShellExecute = false,
        };

        var process = Process.Start(start)
                      ?? throw new InvalidOperationException("Failed to start AirPlay sidecar process.");
        _process = process;
        _logger.LogInformation(
            "AirPlay sidecar started pid={Pid} args={Args}",
            process.Id,
            _spec.Arguments);
    }

    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AirPlay sidecar stop failed");
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
