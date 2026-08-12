using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using RemotePhone.Agent.Core.Configuration;

namespace RemotePhone.Agent.Tests;

public class AgentOptionsTests
{
    [Fact]
    public void FromConfiguration_binds_agent_section_from_json()
    {
        const string json =
            """
            {
              "Agent": {
                "ServerUrl": "wss://example.test/ws/agent",
                "DeviceId": "device-1",
                "AgentId": "agent-1",
                "AgentCredential": "secret",
                "StunServers": [ "stun:stun.l.google.com:19302" ],
                "TurnServers": [ "turn:turn.example.test:3478" ],
                "PreferredResolution": "1280x720",
                "PreferredFps": 30,
                "PreferredBitrate": 2500,
                "FrameQueueCapacity": 5,
                "ReceiverProcessHints": [ "AirServer", "Reflector", "CustomHint" ]
              }
            }
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();

        var options = AgentOptions.FromConfiguration(config);

        options.ServerUrl.Should().Be("wss://example.test/ws/agent");
        options.DeviceId.Should().Be("device-1");
        options.AgentId.Should().Be("agent-1");
        options.AgentCredential.Should().Be("secret");
        options.StunServers.Should().Equal("stun:stun.l.google.com:19302");
        options.TurnServers.Should().Equal("turn:turn.example.test:3478");
        options.PreferredResolution.Should().Be("1280x720");
        options.PreferredFps.Should().Be(30);
        options.PreferredBitrate.Should().Be(2500);
        options.FrameQueueCapacity.Should().Be(5);
        options.ReceiverProcessHints.Should().Equal("AirServer", "Reflector", "CustomHint");
    }

    [Fact]
    public void FromConfiguration_applies_defaults_when_section_empty()
    {
        const string json = """{ "Agent": { } }""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder().AddJsonStream(stream).Build();

        var options = AgentOptions.FromConfiguration(config);

        options.ServerUrl.Should().BeEmpty();
        options.FrameQueueCapacity.Should().Be(3);
        options.ReceiverProcessHints.Should().Equal("AirServer", "Reflector");
        options.StunServers.Should().BeEmpty();
        options.TurnServers.Should().BeEmpty();
    }

    [Fact]
    public void FromConfiguration_throws_when_config_null()
    {
        var act = () => AgentOptions.FromConfiguration(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
