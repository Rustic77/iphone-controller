using System.Text.Json;
using System.Text.Json.Serialization;
using RemotePhone.Agent.Core.Models;

namespace RemotePhone.Agent.Core.Signaling;

public static class SignalingMessageTypes
{
    public const string AgentRegister = "AgentRegister";
    public const string AgentAuthenticated = "AgentAuthenticated";
    public const string StreamStart = "StreamStart";
    public const string StreamStop = "StreamStop";
    public const string WebrtcOffer = "WebrtcOffer";
    public const string WebrtcAnswer = "WebrtcAnswer";
    public const string IceCandidate = "IceCandidate";
    public const string VideoMetadata = "VideoMetadata";
    public const string Heartbeat = "Heartbeat";
    public const string HeartbeatAck = "HeartbeatAck";
    public const string StreamState = "StreamState";
    public const string SourceLost = "SourceLost";
    public const string Error = "Error";
}

public abstract record SignalingMessage
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("agentId")]
    public string? AgentId { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

public sealed record AgentRegisterMessage : SignalingMessage
{
    public override string Type => SignalingMessageTypes.AgentRegister;

    [JsonPropertyName("credential")]
    public string Credential { get; init; } = string.Empty;
}

public sealed record AgentAuthenticatedMessage : SignalingMessage
{
    public override string Type => SignalingMessageTypes.AgentAuthenticated;

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed record StreamStartMessage : SignalingMessage
{
    public override string Type => SignalingMessageTypes.StreamStart;

    [JsonPropertyName("preferredResolution")]
    public string? PreferredResolution { get; init; }

    [JsonPropertyName("preferredFps")]
    public int? PreferredFps { get; init; }

    [JsonPropertyName("preferredBitrate")]
    public int? PreferredBitrate { get; init; }
}

public sealed record StreamStopMessage : SignalingMessage
{
    public override string Type => SignalingMessageTypes.StreamStop;
}

public sealed record WebrtcOfferMessage : SignalingMessage
{
    public override string Type => SignalingMessageTypes.WebrtcOffer;

    [JsonPropertyName("sdp")]
    public string Sdp { get; init; } = string.Empty;
}

public sealed record WebrtcAnswerMessage : SignalingMessage
{
    public override string Type => SignalingMessageTypes.WebrtcAnswer;

    [JsonPropertyName("sdp")]
    public string Sdp { get; init; } = string.Empty;
}

public sealed record IceCandidateMessage : SignalingMessage
{
    public override string Type => SignalingMessageTypes.IceCandidate;

    [JsonPropertyName("candidate")]
    public string Candidate { get; init; } = string.Empty;

    [JsonPropertyName("sdpMid")]
    public string? SdpMid { get; init; }

    [JsonPropertyName("sdpMLineIndex")]
    public int? SdpMLineIndex { get; init; }
}

public sealed record VideoMetadataMessage : SignalingMessage
{
    public override string Type => SignalingMessageTypes.VideoMetadata;

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("orientation")]
    public ScreenOrientation Orientation { get; init; }

    [JsonPropertyName("fps")]
    public double Fps { get; init; }
}

public sealed record HeartbeatMessage : SignalingMessage
{
    public override string Type => SignalingMessageTypes.Heartbeat;

    [JsonPropertyName("timestampUtc")]
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record HeartbeatAckMessage : SignalingMessage
{
    public override string Type => SignalingMessageTypes.HeartbeatAck;

    [JsonPropertyName("timestampUtc")]
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record StreamStateMessage : SignalingMessage
{
    public override string Type => SignalingMessageTypes.StreamState;

    [JsonPropertyName("state")]
    public CaptureState State { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

public sealed record SourceLostMessage : SignalingMessage
{
    public override string Type => SignalingMessageTypes.SourceLost;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record ErrorMessage : SignalingMessage
{
    public override string Type => SignalingMessageTypes.Error;

    [JsonPropertyName("code")]
    public string Code { get; init; } = "error";

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    /// <summary>Hub errors use <c>reason</c> instead of <c>message</c>.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonIgnore]
    public string Display =>
        !string.IsNullOrWhiteSpace(Message) ? Message :
        !string.IsNullOrWhiteSpace(Reason) ? Reason! :
        Code;
}

public static class SignalingMessageSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false,
    };

    public static string Serialize(SignalingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.Serialize(message, message.GetType(), Options);
    }

    public static SignalingMessage Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("type", out var typeElement))
        {
            throw new JsonException("Signaling message is missing required 'type' property.");
        }

        var type = CanonicalizeType(typeElement.GetString()
            ?? throw new JsonException("Signaling message 'type' property was null."));

        return type switch
        {
            SignalingMessageTypes.AgentRegister => DeserializeAs<AgentRegisterMessage>(json),
            SignalingMessageTypes.AgentAuthenticated => DeserializeAuthenticated(json, originalType: typeElement.GetString()),
            SignalingMessageTypes.StreamStart => DeserializeAs<StreamStartMessage>(json),
            SignalingMessageTypes.StreamStop => DeserializeAs<StreamStopMessage>(json),
            SignalingMessageTypes.WebrtcOffer => DeserializeAs<WebrtcOfferMessage>(json),
            SignalingMessageTypes.WebrtcAnswer => DeserializeAs<WebrtcAnswerMessage>(json),
            SignalingMessageTypes.IceCandidate => DeserializeAs<IceCandidateMessage>(json),
            SignalingMessageTypes.VideoMetadata => DeserializeAs<VideoMetadataMessage>(json),
            SignalingMessageTypes.Heartbeat => DeserializeAs<HeartbeatMessage>(json),
            SignalingMessageTypes.HeartbeatAck => DeserializeAs<HeartbeatAckMessage>(json),
            SignalingMessageTypes.StreamState => DeserializeAs<StreamStateMessage>(json),
            SignalingMessageTypes.SourceLost => DeserializeAs<SourceLostMessage>(json),
            SignalingMessageTypes.Error => DeserializeAs<ErrorMessage>(json),
            _ => throw new JsonException($"Unknown signaling message type '{typeElement.GetString()}'."),
        };
    }

    /// <summary>
    /// The hub speaks snake_case (<c>stream_start</c>); the agent historically spoke PascalCase.
    /// Accept both so the Windows agent can actually receive StreamStart / SDP / ICE.
    /// </summary>
    public static string CanonicalizeType(string type) => type switch
    {
        "register" or "AgentRegister" => SignalingMessageTypes.AgentRegister,
        "registered" or "AgentAuthenticated" => SignalingMessageTypes.AgentAuthenticated,
        "stream_start" or "StreamStart" => SignalingMessageTypes.StreamStart,
        "stream_stop" or "StreamStop" => SignalingMessageTypes.StreamStop,
        "webrtc_offer" or "WebrtcOffer" => SignalingMessageTypes.WebrtcOffer,
        "webrtc_answer" or "WebrtcAnswer" => SignalingMessageTypes.WebrtcAnswer,
        "ice_candidate" or "IceCandidate" => SignalingMessageTypes.IceCandidate,
        "video_metadata" or "VideoMetadata" => SignalingMessageTypes.VideoMetadata,
        "heartbeat" or "Heartbeat" => SignalingMessageTypes.Heartbeat,
        "heartbeat_ack" or "HeartbeatAck" => SignalingMessageTypes.HeartbeatAck,
        "stream_state" or "StreamState" => SignalingMessageTypes.StreamState,
        "source_lost" or "SourceLost" => SignalingMessageTypes.SourceLost,
        "error" or "Error" => SignalingMessageTypes.Error,
        _ => type,
    };

    private static AgentAuthenticatedMessage DeserializeAuthenticated(string json, string? originalType)
    {
        var msg = DeserializeAs<AgentAuthenticatedMessage>(json);
        // Hub sends { type: "registered" } with no success field; header auth already succeeded.
        if (string.Equals(originalType, "registered", StringComparison.OrdinalIgnoreCase))
        {
            return msg with { Success = true };
        }

        return msg;
    }

    private static T DeserializeAs<T>(string json) where T : SignalingMessage
        => JsonSerializer.Deserialize<T>(json, Options)
           ?? throw new JsonException($"Failed to deserialize signaling message as {typeof(T).Name}.");
}

/// <summary>
/// Accepts messages only for the currently active session; rejects stale session IDs.
/// </summary>
public sealed class SessionGate
{
    private readonly object _gate = new();
    private string? _currentSessionId;

    public string? CurrentSessionId
    {
        get
        {
            lock (_gate)
            {
                return _currentSessionId;
            }
        }
    }

    public void SetSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            _currentSessionId = sessionId;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _currentSessionId = null;
        }
    }

    public bool Accept(string? sessionId)
    {
        lock (_gate)
        {
            if (_currentSessionId is null)
            {
                return false;
            }

            return string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal);
        }
    }
}
