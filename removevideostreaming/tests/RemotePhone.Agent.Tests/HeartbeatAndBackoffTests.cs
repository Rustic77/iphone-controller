using FluentAssertions;
using RemotePhone.Agent.Core.Reliability;
using RemotePhone.Agent.Core.Signaling;

namespace RemotePhone.Agent.Tests;

public class HeartbeatAndBackoffTests
{
    [Fact]
    public void ExponentialBackoff_increases_without_jitter()
    {
        var backoff = new ExponentialBackoff(minDelayMs: 100, maxDelayMs: 10_000, jitterRatio: 0);

        var d1 = backoff.NextDelayMs();
        var d2 = backoff.NextDelayMs();
        var d3 = backoff.NextDelayMs();
        var d4 = backoff.NextDelayMs();

        d1.Should().Be(100);
        d2.Should().Be(200);
        d3.Should().Be(400);
        d4.Should().Be(800);
        backoff.Attempt.Should().Be(4);
    }

    [Fact]
    public void ExponentialBackoff_caps_at_maxDelay()
    {
        var backoff = new ExponentialBackoff(minDelayMs: 1000, maxDelayMs: 3000, jitterRatio: 0);

        backoff.NextDelayMs().Should().Be(1000);
        backoff.NextDelayMs().Should().Be(2000);
        backoff.NextDelayMs().Should().Be(3000);
        backoff.NextDelayMs().Should().Be(3000);
    }

    [Fact]
    public void ExponentialBackoff_Reset_restarts_sequence()
    {
        var backoff = new ExponentialBackoff(minDelayMs: 50, maxDelayMs: 5000, jitterRatio: 0);
        backoff.NextDelayMs();
        backoff.NextDelayMs();
        backoff.Reset();
        backoff.Attempt.Should().Be(0);
        backoff.NextDelayMs().Should().Be(50);
    }

    [Fact]
    public void Heartbeat_message_types_roundtrip()
    {
        SignalingMessageTypes.Heartbeat.Should().Be("Heartbeat");
        SignalingMessageTypes.HeartbeatAck.Should().Be("HeartbeatAck");

        var ts = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        var heartbeat = new HeartbeatMessage
        {
            DeviceId = "d1",
            AgentId = "a1",
            SessionId = "s1",
            TimestampUtc = ts,
        };
        var ack = new HeartbeatAckMessage
        {
            DeviceId = "d1",
            AgentId = "a1",
            SessionId = "s1",
            TimestampUtc = ts.AddMilliseconds(5),
        };

        var hbRestored = SignalingMessageSerializer.Deserialize(SignalingMessageSerializer.Serialize(heartbeat))
            .Should().BeOfType<HeartbeatMessage>().Subject;
        hbRestored.Type.Should().Be(SignalingMessageTypes.Heartbeat);
        hbRestored.TimestampUtc.Should().Be(ts);

        var ackRestored = SignalingMessageSerializer.Deserialize(SignalingMessageSerializer.Serialize(ack))
            .Should().BeOfType<HeartbeatAckMessage>().Subject;
        ackRestored.Type.Should().Be(SignalingMessageTypes.HeartbeatAck);
        ackRestored.TimestampUtc.Should().Be(ts.AddMilliseconds(5));
    }
}
