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

        var type = typeElement.GetString()
            ?? throw new JsonException("Signaling message 'type' property was null.");

        return type switch
        {
            SignalingMessageTypes.AgentRegister => DeserializeAs<AgentRegisterMessage>(json),
            SignalingMessageTypes.AgentAuthenticated => DeserializeAs<AgentAuthenticatedMessage>(json),
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
            _ => throw new JsonException($"Unknown signaling message type '{type}'."),
        };
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
