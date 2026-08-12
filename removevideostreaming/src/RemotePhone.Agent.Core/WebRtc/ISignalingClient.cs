namespace RemotePhone.Agent.Core.WebRtc;

public interface ISignalingClient
{
    bool IsConnected { get; }

    event EventHandler<object>? MessageReceived;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SendAsync(object message, CancellationToken cancellationToken = default);
}
