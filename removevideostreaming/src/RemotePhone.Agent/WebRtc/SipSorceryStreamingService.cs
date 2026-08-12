using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Logging;
using RemotePhone.Agent.Core.Configuration;
using RemotePhone.Agent.Core.Reliability;
using RemotePhone.Agent.Core.Signaling;
using RemotePhone.Agent.Core.WebRtc;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RemotePhone_Agent.WebRtc;

/// <summary>
/// WebRTC streaming via SIPSorcery <see cref="RTCPeerConnection"/>.
/// Captured frames feed <see cref="NotifyFrame"/> for stats and attempt VP8 send via <see cref="CaptureVideoSource"/>.
/// When encoding fails, a test-pattern/placeholder path keeps the peer connection alive for signaling milestones.
/// Capture remains healthy independently when WebRTC drops.
/// </summary>
public sealed class SipSorceryStreamingService : IWebRtcStreamingService
{
    private readonly AgentOptions _options;
    private readonly ISignalingClient _signaling;
    private readonly ILogger _logger;
    private readonly ExponentialBackoff _backoff = new();
    private readonly SessionGate _sessionGate = new();
    private readonly object _gate = new();
    private readonly Stopwatch _fpsWatch = Stopwatch.StartNew();
    private int _framesInWindow;

    private RTCPeerConnection? _pc;
    private CaptureVideoSource? _videoSource;
    private CancellationTokenSource? _cts;
    private bool _sourceLost;
    private bool _disposed;

    public SipSorceryStreamingService(AgentOptions options, ISignalingClient signaling, ILogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _signaling.MessageReceived += OnSignalingMessage;
    }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public double StreamFps { get; private set; }
    public double BitrateKbps { get; private set; }
    public double RttMs { get; private set; }
    public long DroppedFrames { get; private set; }

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<Exception>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        SetState(ConnectionState.Connecting);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sourceLost = false;
        _backoff.Reset();

        if (!_signaling.IsConnected)
        {
            await _signaling.ConnectAsync(_cts.Token).ConfigureAwait(false);
            await _signaling.SendAsync(new AgentRegisterMessage
            {
                AgentId = _options.AgentId,
                DeviceId = _options.DeviceId,
                Credential = _options.AgentCredential,
            }, _cts.Token).ConfigureAwait(false);
        }

        await CreatePeerConnectionAsync(_cts.Token).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Disconnected);
        await TeardownPeerAsync().ConfigureAwait(false);
        _sessionGate.Clear();

        try
        {
            await _signaling.SendAsync(new StreamStopMessage
            {
                AgentId = _options.AgentId,
                DeviceId = _options.DeviceId,
                SessionId = _sessionGate.CurrentSessionId,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StreamStop send failed during StopAsync");
        }
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        SetState(ConnectionState.Reconnecting);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TeardownPeerAsync().ConfigureAwait(false);
                var delay = _backoff.NextDelayMs();
                _logger.LogInformation("WebRTC reconnect attempt {Attempt} after {DelayMs}ms", _backoff.Attempt, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                await StartAsync(cancellationToken).ConfigureAwait(false);
                _backoff.Reset();
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebRTC reconnect failed");
                Faulted?.Invoke(this, ex);
                SetState(ConnectionState.Reconnecting);
            }
        }

        SetState(ConnectionState.Failed);
    }

    public void NotifyFrame(int width, int height)
    {
        if (_sourceLost || State is not (ConnectionState.Connected or ConnectionState.Connecting))
        {
            DroppedFrames++;
            return;
        }

        _framesInWindow++;
        if (_fpsWatch.ElapsedMilliseconds >= 1000)
        {
            StreamFps = _framesInWindow * 1000.0 / _fpsWatch.ElapsedMilliseconds;
            BitrateKbps = Math.Max(0, (_options.PreferredBitrate / 1000.0) * (StreamFps / Math.Max(1, _options.PreferredFps)));
            _framesInWindow = 0;
            _fpsWatch.Restart();
        }

        try
        {
            _videoSource?.PushBgraFrame(width, height, bgra: null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NotifyFrame encode/push failed; capture continues");
            DroppedFrames++;
        }
    }

    /// <summary>
    /// Optional path: push real BGRA pixels when the capture preview converter provides them.
    /// </summary>
    public void PushBgraFrame(int width, int height, byte[] bgra)
    {
        if (_sourceLost)
        {
            return;
        }

        try
        {
            _videoSource?.PushBgraFrame(width, height, bgra);
            NotifyFrame(width, height);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PushBgraFrame failed");
            DroppedFrames++;
        }
    }

    public void NotifySourceLost(string? reason = null)
    {
        _sourceLost = true;
        _ = SafeSendAsync(new SourceLostMessage
        {
            AgentId = _options.AgentId,
            DeviceId = _options.DeviceId,
            SessionId = _sessionGate.CurrentSessionId,
            Reason = reason,
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _signaling.MessageReceived -= OnSignalingMessage;
        _cts?.Cancel();
        _cts?.Dispose();
        _ = TeardownPeerAsync();
    }

    private async Task CreatePeerConnectionAsync(CancellationToken cancellationToken)
    {
        var iceServers = new List<RTCIceServer>();
        foreach (var stun in _options.StunServers ?? [])
        {
            if (!string.IsNullOrWhiteSpace(stun))
            {
                iceServers.Add(new RTCIceServer { urls = stun });
            }
        }

        foreach (var turn in _options.TurnServers ?? [])
        {
            if (!string.IsNullOrWhiteSpace(turn))
            {
                iceServers.Add(new RTCIceServer { urls = turn });
            }
        }

        var config = new RTCConfiguration { iceServers = iceServers };
        var pc = new RTCPeerConnection(config);
        var videoSource = new CaptureVideoSource();

        var track = new MediaStreamTrack(videoSource.GetVideoSourceFormats(), MediaStreamStatusEnum.SendOnly);
        pc.addTrack(track);
        videoSource.OnVideoSourceEncodedSample += pc.SendVideo;
        pc.OnVideoFormatsNegotiated += formats =>
        {
            var format = formats.FirstOrDefault();
            if (format.Codec != VideoCodecsEnum.Unknown)
            {
                videoSource.SetVideoSourceFormat(format);
            }
        };

        pc.onicecandidate += candidate =>
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.candidate))
            {
                return;
            }

            _ = SafeSendAsync(new IceCandidateMessage
            {
                AgentId = _options.AgentId,
                DeviceId = _options.DeviceId,
                SessionId = _sessionGate.CurrentSessionId,
                Candidate = candidate.candidate,
                SdpMid = candidate.sdpMid,
                SdpMLineIndex = candidate.sdpMLineIndex,
            });
        };

        pc.onconnectionstatechange += state =>
        {
            _logger.LogInformation("RTCPeerConnection state={State}", state);
            switch (state)
            {
                case RTCPeerConnectionState.connected:
                    SetState(ConnectionState.Connected);
                    _ = videoSource.StartVideo();
                    break;
                case RTCPeerConnectionState.failed:
                    SetState(ConnectionState.Failed);
                    break;
                case RTCPeerConnectionState.disconnected:
                    SetState(ConnectionState.Reconnecting);
                    break;
                case RTCPeerConnectionState.closed:
                    SetState(ConnectionState.Disconnected);
                    break;
            }
        };

        lock (_gate)
        {
            _pc = pc;
            _videoSource = videoSource;
        }

        var sessionId = Guid.NewGuid().ToString("N");
        _sessionGate.SetSession(sessionId);

        await _signaling.SendAsync(new StreamStartMessage
        {
            AgentId = _options.AgentId,
            DeviceId = _options.DeviceId,
            SessionId = sessionId,
            PreferredResolution = _options.PreferredResolution,
            PreferredFps = _options.PreferredFps,
            PreferredBitrate = _options.PreferredBitrate,
        }, cancellationToken).ConfigureAwait(false);

        var offer = pc.createOffer();
        await pc.setLocalDescription(offer).ConfigureAwait(false);

        await _signaling.SendAsync(new WebrtcOfferMessage
        {
            AgentId = _options.AgentId,
            DeviceId = _options.DeviceId,
            SessionId = sessionId,
            Sdp = offer.sdp ?? string.Empty,
        }, cancellationToken).ConfigureAwait(false);

        // Kick a placeholder frame so encoders initialize even before capture notifies.
        videoSource.PushBgraFrame(640, 360, bgra: null);
        SetState(ConnectionState.Connecting);
    }

    private void OnSignalingMessage(object? sender, object message)
    {
        try
        {
            switch (message)
            {
                case WebrtcAnswerMessage answer:
                    if (!_sessionGate.Accept(answer.SessionId) && answer.SessionId is not null)
                    {
                        _logger.LogDebug("Ignoring stale WebrtcAnswer session={SessionId}", answer.SessionId);
                        return;
                    }

                    var pc = _pc;
                    if (pc is null)
                    {
                        return;
                    }

                    var sdp = SDP.ParseSDPDescription(answer.Sdp);
                    var result = pc.setRemoteDescription(new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.answer,
                        sdp = answer.Sdp,
                    });
                    if (result != SetDescriptionResultEnum.OK)
                    {
                        _logger.LogWarning("setRemoteDescription failed: {Result}", result);
                    }

                    _ = sdp;
                    break;

                case IceCandidateMessage ice:
                    if (!_sessionGate.Accept(ice.SessionId) && ice.SessionId is not null)
                    {
                        return;
                    }

                    _pc?.addIceCandidate(new RTCIceCandidateInit
                    {
                        candidate = ice.Candidate,
                        sdpMid = ice.SdpMid,
                        sdpMLineIndex = (ushort)(ice.SdpMLineIndex ?? 0),
                    });
                    break;

                case AgentAuthenticatedMessage auth:
                    _logger.LogInformation("AgentAuthenticated Success={Success} Message={Message}", auth.Success, auth.Message);
                    break;

                case ErrorMessage error:
                    _logger.LogWarning("Signaling error {Code}: {Message}", error.Code, error.Message);
                    Faulted?.Invoke(this, new InvalidOperationException(error.Message));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed handling signaling message");
            Faulted?.Invoke(this, ex);
        }
    }

    private async Task TeardownPeerAsync()
    {
        CaptureVideoSource? source;
        RTCPeerConnection? pc;
        lock (_gate)
        {
            source = _videoSource;
            pc = _pc;
            _videoSource = null;
            _pc = null;
        }

        if (source is not null)
        {
            try
            {
                if (pc is not null)
                {
                    source.OnVideoSourceEncodedSample -= pc.SendVideo;
                }

                await source.CloseVideo().ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            source.Dispose();
        }

        if (pc is not null)
        {
            try
            {
                pc.Close("teardown");
            }
            catch
            {
                // ignore
            }

            pc.Dispose();
        }
    }

    private async Task SafeSendAsync(SignalingMessage message)
    {
        try
        {
            if (_signaling.IsConnected)
            {
                await _signaling.SendAsync(message).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Signaling send failed for {Type}", message.Type);
        }
    }

    private void SetState(ConnectionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
