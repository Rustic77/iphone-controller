using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Logging;
using RemotePhone.Agent.Core.Configuration;
using RemotePhone.Agent.Core.Models;
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
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private bool _sourceLost;
    private bool _disposed;
    private int _signalingReconnectBusy;

    public SipSorceryStreamingService(AgentOptions options, ISignalingClient signaling, ILogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _signaling.MessageReceived += OnSignalingMessage;
        _signaling.Disconnected += OnSignalingDisconnected;
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

        // Do not create a peer connection yet. The hub assigns the video
        // sessionId via stream_start; offering with a local Guid is rejected
        // as stale_session and never reaches the browser.
        _logger.LogInformation("Signaling connected; waiting for stream_start");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        SetState(ConnectionState.Disconnected);
        var sessionId = _sessionGate.CurrentSessionId;
        await TeardownPeerAsync().ConfigureAwait(false);
        _sessionGate.Clear();

        try
        {
            await _signaling.SendAsync(new StreamStopMessage
            {
                AgentId = _options.AgentId,
                DeviceId = _options.DeviceId,
                SessionId = sessionId,
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

    private void OnSignalingDisconnected(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var cts = _cts;
        if (cts is null || cts.IsCancellationRequested)
        {
            return;
        }

        if (Interlocked.Exchange(ref _signalingReconnectBusy, 1) == 1)
        {
            return;
        }

        _logger.LogWarning("Hub signaling dropped; reconnecting so the control panel can see this phone");
        _ = ReconnectSignalingAsync(cts.Token);
    }

    private async Task ReconnectSignalingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signaling reconnect ended");
        }
        finally
        {
            Interlocked.Exchange(ref _signalingReconnectBusy, 0);
        }
    }

    public void NotifyFrame(int width, int height)
    {
        if (_sourceLost || State is not (ConnectionState.Connected or ConnectionState.Connecting))
        {
            DroppedFrames++;
        }

        _ = width;
        _ = height;
        // Stats-only. Encoding a placeholder here would flash a test pattern over real video.
    }

    public void PushBgraFrame(int width, int height, byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
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
            _videoSource?.PushBgraFrame(width, height, bgra);
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
        _signaling.Disconnected -= OnSignalingDisconnected;
        _cts?.Cancel();
        _cts?.Dispose();
        _ = TeardownPeerAsync();
    }

    private async Task HandleStreamStartAsync(StreamStartMessage start)
    {
        if (string.IsNullOrWhiteSpace(start.SessionId))
        {
            _logger.LogWarning("Ignoring stream_start with empty sessionId");
            return;
        }

        await _sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _sourceLost = false;
            await TeardownPeerAsync().ConfigureAwait(false);
            _sessionGate.SetSession(start.SessionId);
            var token = _cts?.Token ?? CancellationToken.None;
            _logger.LogInformation("stream_start session={SessionId}", start.SessionId);
            await CreatePeerConnectionAsync(start.SessionId, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start WebRTC session {SessionId}", start.SessionId);
            Faulted?.Invoke(this, ex);
            SetState(ConnectionState.Failed);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task HandleStreamStopAsync(StreamStopMessage stop)
    {
        await _sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (stop.SessionId is not null && !_sessionGate.Accept(stop.SessionId))
            {
                _logger.LogDebug("Ignoring stale stream_stop session={SessionId}", stop.SessionId);
                return;
            }

            await TeardownPeerAsync().ConfigureAwait(false);
            _sessionGate.Clear();
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task CreatePeerConnectionAsync(string sessionId, CancellationToken cancellationToken)
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

        await videoSource.StartVideo().ConfigureAwait(false);

        var offer = pc.createOffer();
        await pc.setLocalDescription(offer).ConfigureAwait(false);

        await _signaling.SendAsync(new WebrtcOfferMessage
        {
            AgentId = _options.AgentId,
            DeviceId = _options.DeviceId,
            SessionId = sessionId,
            Sdp = offer.sdp ?? string.Empty,
        }, cancellationToken).ConfigureAwait(false);

        // Kick a placeholder so the encoder initializes before the first capture frame.
        videoSource.PushBgraFrame(640, 360, bgra: null);
        SetState(ConnectionState.Connecting);
        _ = SafeSendAsync(new StreamStateMessage
        {
            AgentId = _options.AgentId,
            DeviceId = _options.DeviceId,
            SessionId = sessionId,
            State = CaptureState.Capturing,
            Detail = "offering",
        });
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

                case StreamStartMessage start:
                    _ = HandleStreamStartAsync(start);
                    break;

                case StreamStopMessage stop:
                    _ = HandleStreamStopAsync(stop);
                    break;

                case ErrorMessage error:
                    _logger.LogWarning("Signaling error {Code}: {Message}", error.Code, error.Display);
                    Faulted?.Invoke(this, new InvalidOperationException(error.Display));
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
