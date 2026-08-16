namespace RemotePhone.Agent.Core.WebRtc;

public interface ISignalingClient
{
    bool IsConnected { get; }

    event EventHandler<object>? MessageReceived;

    /// <summary>Raised when the socket drops unexpectedly (not after a local DisconnectAsync).</summary>
    event EventHandler? Disconnected;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SendAsync(object message, CancellationToken cancellationToken = default);
}
