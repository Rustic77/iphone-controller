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
    /// Stats-only hook when pixel data is not available. Must not encode a placeholder
    /// in place of real capture frames.
    /// </summary>
    void NotifyFrame(int width, int height);

    /// <summary>
    /// Encode and send a tightly packed BGRA8 frame (CPU path).
    /// </summary>
    void PushBgraFrame(int width, int height, byte[] bgra);
}
