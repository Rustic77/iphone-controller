using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using RemotePhone.Agent.Core.Configuration;
using RemotePhone.Agent.Core.Signaling;
using RemotePhone.Agent.Core.WebRtc;

namespace RemotePhone_Agent.Signaling;

/// <summary>
/// <see cref="ISignalingClient"/> backed by <see cref="ClientWebSocket"/> and Core JSON messages.
/// Authenticates to controllerplatform <c>/ws/agent</c> with device/agent headers.
/// </summary>
public sealed class WebSocketSignalingClient : ISignalingClient, IAsyncDisposable
{
    private readonly Uri _serverUri;
    private readonly AgentOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoop;

    public WebSocketSignalingClient(AgentOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServerUrl);
        _options = options;
        _serverUri = new Uri(options.ServerUrl);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Backward-compatible ctor when only the URL is known (headers unset).</summary>
    public WebSocketSignalingClient(string serverUrl, ILogger logger)
        : this(new AgentOptions { ServerUrl = serverUrl }, logger)
    {
    }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public event EventHandler<object>? MessageReceived;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return;
        }

        await DisconnectAsync(cancellationToken).ConfigureAwait(false);

        var socket = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(_options.DeviceId))
        {
            socket.Options.SetRequestHeader("x-device-id", _options.DeviceId);
        }

        if (!string.IsNullOrWhiteSpace(_options.AgentId))
        {
            socket.Options.SetRequestHeader("x-agent-id", _options.AgentId);
        }

        if (!string.IsNullOrWhiteSpace(_options.AgentCredential))
        {
            socket.Options.SetRequestHeader("x-agent-secret", _options.AgentCredential);
        }

        await socket.ConnectAsync(_serverUri, cancellationToken).ConfigureAwait(false);
        _socket = socket;
        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), CancellationToken.None);
        _logger.LogInformation("Signaling connected to {ServerUrl}", _serverUri);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var cts = _receiveCts;
        _receiveCts = null;
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        var loop = _receiveLoop;
        _receiveLoop = null;
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }

        cts?.Dispose();

        var socket = _socket;
        _socket = null;
        if (socket is not null)
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Signaling close failed");
            }
            finally
            {
                socket.Dispose();
            }
        }
    }

    public async Task SendAsync(object message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Signaling socket is not connected.");
        }

        string json = message switch
        {
            SignalingMessage signaling => SignalingMessageSerializer.Serialize(signaling),
            string s => s,
            _ => System.Text.Json.JsonSerializer.Serialize(message),
        };

        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(ms.ToArray());
                try
                {
                    var message = SignalingMessageSerializer.Deserialize(json);
                    MessageReceived?.Invoke(this, message);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize signaling message");
                    MessageReceived?.Invoke(this, json);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on disconnect
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signaling receive loop ended");
        }
    }
}
