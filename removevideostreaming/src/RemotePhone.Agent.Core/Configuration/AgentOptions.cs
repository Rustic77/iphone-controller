using Microsoft.Extensions.Configuration;
using RemotePhone.Agent.Core.AirPlay;

namespace RemotePhone.Agent.Core.Configuration;

public sealed class AgentOptions
{
    public string ServerUrl { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string AgentCredential { get; set; } = string.Empty;
    public string[] StunServers { get; set; } = [];
    public string[] TurnServers { get; set; } = [];
    public string? PreferredResolution { get; set; }
    public int PreferredFps { get; set; }
    public int PreferredBitrate { get; set; }
    public int FrameQueueCapacity { get; set; } = 3;
    public string[] ReceiverProcessHints { get; set; } = ["AirServer", "Reflector", "airplay-windows"];
    public string AirPlaySidecarUrl { get; set; } = AirPlaySidecarSpec.DefaultDownloadUrl;
    public string AirPlaySidecarSha256 { get; set; } = AirPlaySidecarSpec.DefaultSha256;
    public string AirPlaySidecarArguments { get; set; } = AirPlaySidecarSpec.DefaultArguments;

    public static AgentOptions FromConfiguration(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Clear collection defaults before Bind so JSON arrays replace instead of append.
        var options = new AgentOptions
        {
            ReceiverProcessHints = [],
            StunServers = [],
            TurnServers = [],
        };
        config.GetSection("Agent").Bind(options);

        if (options.ReceiverProcessHints.Length == 0)
        {
            options.ReceiverProcessHints = ["AirServer", "Reflector", "airplay-windows"];
        }

        if (string.IsNullOrWhiteSpace(options.AirPlaySidecarUrl))
        {
            options.AirPlaySidecarUrl = AirPlaySidecarSpec.DefaultDownloadUrl;
        }

        if (string.IsNullOrWhiteSpace(options.AirPlaySidecarSha256))
        {
            options.AirPlaySidecarSha256 = AirPlaySidecarSpec.DefaultSha256;
        }

        if (string.IsNullOrWhiteSpace(options.AirPlaySidecarArguments))
        {
            options.AirPlaySidecarArguments = AirPlaySidecarSpec.DefaultArguments;
        }

        return options;
    }
}
