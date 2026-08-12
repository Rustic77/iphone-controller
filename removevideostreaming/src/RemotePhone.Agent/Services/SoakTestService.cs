using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RemotePhone.Agent.Core.Reliability;

namespace RemotePhone_Agent.Services;

/// <summary>
/// Long-running soak sampler. Prefer current metrics over delayed ones; never grows unbounded.
/// </summary>
public sealed class SoakTestService
{
    private readonly ILogger _logger;
    private readonly Stopwatch _runtime = new();
    private readonly SoakMetrics _metrics = new();
    private Timer? _timer;

    public SoakTestService(ILogger logger)
    {
        _logger = logger;
    }

    public SoakMetrics Snapshot => _metrics;

    public bool IsRunning => _timer is not null;

    public void Start(Func<SoakSample> sampleProvider, TimeSpan interval)
    {
        Stop();
        _runtime.Restart();
        _timer = new Timer(_ =>
        {
            try
            {
                var sample = sampleProvider();
                _metrics.Runtime = _runtime.Elapsed;
                _metrics.CaptureFps = sample.CaptureFps;
                _metrics.StreamFps = sample.StreamFps;
                _metrics.DroppedFrames = sample.DroppedFrames;
                _metrics.ReconnectCount = sample.ReconnectCount;
                _metrics.SourceReconnectCount = sample.SourceReconnectCount;
                _metrics.ErrorCount = sample.ErrorCount;
                _metrics.MemoryBytes = GC.GetTotalMemory(forceFullCollection: false);
                _logger.LogInformation(
                    "soak runtime={Runtime} captureFps={CaptureFps} streamFps={StreamFps} dropped={Dropped} mem={Mem}",
                    _metrics.Runtime,
                    _metrics.CaptureFps,
                    _metrics.StreamFps,
                    _metrics.DroppedFrames,
                    _metrics.MemoryBytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "soak sample failed");
            }
        }, null, interval, interval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _runtime.Stop();
    }
}

public readonly record struct SoakSample(
    double CaptureFps,
    double StreamFps,
    long DroppedFrames,
    int ReconnectCount,
    int SourceReconnectCount,
    int ErrorCount);
