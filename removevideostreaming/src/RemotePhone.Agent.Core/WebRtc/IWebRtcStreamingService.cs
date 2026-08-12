namespace RemotePhone.Agent.Core.WebRtc;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Failed,
}

public interface IWebRtcStreamingService : IDisposable
{
    ConnectionState State { get; }
    double StreamFps { get; }
    double BitrateKbps { get; }
    double RttMs { get; }
    long DroppedFrames { get; }

    event EventHandler<ConnectionState>? StateChanged;
    event EventHandler<Exception>? Faulted;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task ReconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Hook for push-frame stats; implementations update FPS / dimension telemetry.
    /// </summary>
    void NotifyFrame(int width, int height);
}
