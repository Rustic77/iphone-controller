using FluentAssertions;
using RemotePhone.Agent.Core.Models;
using RemotePhone.Agent.Core.Signaling;

namespace RemotePhone.Agent.Tests;

public class SignalingSerializerTests
{
    [Fact]
    public void Roundtrip_AgentRegister()
    {
        var original = new AgentRegisterMessage
        {
            DeviceId = "d1",
            AgentId = "a1",
            Credential = "cred",
        };

        var json = SignalingMessageSerializer.Serialize(original);
        var restored = SignalingMessageSerializer.Deserialize(json).Should().BeOfType<AgentRegisterMessage>().Subject;

        restored.DeviceId.Should().Be("d1");
        restored.AgentId.Should().Be("a1");
        restored.Credential.Should().Be("cred");
        restored.Type.Should().Be(SignalingMessageTypes.AgentRegister);
    }

    [Fact]
    public void Roundtrip_WebrtcOffer_and_IceCandidate()
    {
        var offer = new WebrtcOfferMessage
        {
            SessionId = "s1",
            DeviceId = "d1",
            AgentId = "a1",
            Sdp = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\n",
        };

        var ice = new IceCandidateMessage
        {
            SessionId = "s1",
            Candidate = "candidate:1 1 UDP 2122252543 192.168.1.2 54400 typ host",
            SdpMid = "0",
            SdpMLineIndex = 0,
        };

        var offerRestored = SignalingMessageSerializer.Deserialize(SignalingMessageSerializer.Serialize(offer))
            .Should().BeOfType<WebrtcOfferMessage>().Subject;
        offerRestored.Sdp.Should().StartWith("v=0");
        offerRestored.SessionId.Should().Be("s1");

        var iceRestored = SignalingMessageSerializer.Deserialize(SignalingMessageSerializer.Serialize(ice))
            .Should().BeOfType<IceCandidateMessage>().Subject;
        iceRestored.Candidate.Should().Contain("typ host");
        iceRestored.SdpMid.Should().Be("0");
        iceRestored.SdpMLineIndex.Should().Be(0);
    }

    [Fact]
    public void Roundtrip_VideoMetadata_and_StreamState()
    {
        var meta = new VideoMetadataMessage
        {
            SessionId = "s1",
            Width = 1170,
            Height = 2532,
            Orientation = ScreenOrientation.Portrait,
            Fps = 29.97,
        };

        var state = new StreamStateMessage
        {
            SessionId = "s1",
            State = CaptureState.Capturing,
            Detail = "ok",
        };

        var metaRestored = SignalingMessageSerializer.Deserialize(SignalingMessageSerializer.Serialize(meta))
            .Should().BeOfType<VideoMetadataMessage>().Subject;
        metaRestored.Width.Should().Be(1170);
        metaRestored.Orientation.Should().Be(ScreenOrientation.Portrait);

        var stateRestored = SignalingMessageSerializer.Deserialize(SignalingMessageSerializer.Serialize(state))
            .Should().BeOfType<StreamStateMessage>().Subject;
        stateRestored.State.Should().Be(CaptureState.Capturing);
        stateRestored.Detail.Should().Be("ok");
    }

    [Fact]
    public void Deserialize_hub_snake_case_stream_start_and_answer()
    {
        var start = SignalingMessageSerializer.Deserialize(
                """{"type":"stream_start","sessionId":"hub-session","deviceId":"esp32-lab-01"}""")
            .Should().BeOfType<StreamStartMessage>().Subject;
        start.SessionId.Should().Be("hub-session");
        start.DeviceId.Should().Be("esp32-lab-01");

        var answer = SignalingMessageSerializer.Deserialize(
                """{"type":"webrtc_answer","sessionId":"hub-session","sdp":"v=0","deviceId":"esp32-lab-01"}""")
            .Should().BeOfType<WebrtcAnswerMessage>().Subject;
        answer.Sdp.Should().Be("v=0");
        answer.SessionId.Should().Be("hub-session");

        var ice = SignalingMessageSerializer.Deserialize(
                """{"type":"ice_candidate","sessionId":"hub-session","candidate":"candidate:1","sdpMid":"0","sdpMLineIndex":0}""")
            .Should().BeOfType<IceCandidateMessage>().Subject;
        ice.Candidate.Should().Be("candidate:1");
    }

    [Fact]
    public void Deserialize_hub_registered_is_authenticated_success()
    {
        var auth = SignalingMessageSerializer.Deserialize(
                """{"type":"registered","deviceId":"esp32-lab-01","agentId":"windows-agent-01"}""")
            .Should().BeOfType<AgentAuthenticatedMessage>().Subject;
        auth.Success.Should().BeTrue();
        auth.DeviceId.Should().Be("esp32-lab-01");
        auth.AgentId.Should().Be("windows-agent-01");
    }

    [Fact]
    public void Deserialize_hub_error_reason()
    {
        var err = SignalingMessageSerializer.Deserialize(
                """{"type":"error","reason":"stale_session"}""")
            .Should().BeOfType<ErrorMessage>().Subject;
        err.Display.Should().Be("stale_session");
    }

    [Fact]
    public void Deserialize_unknown_type_throws()
    {
        var act = () => SignalingMessageSerializer.Deserialize("""{"type":"Nope"}""");
        act.Should().Throw<System.Text.Json.JsonException>().WithMessage("*Unknown*");
    }

    [Fact]
    public void SessionGate_rejects_stale_and_accepts_current()
    {
        var gate = new SessionGate();
        gate.Accept("any").Should().BeFalse("no session set yet");

        gate.SetSession("session-current");
        gate.CurrentSessionId.Should().Be("session-current");
        gate.Accept("session-current").Should().BeTrue();
        gate.Accept("session-stale").Should().BeFalse();
        gate.Accept(null).Should().BeFalse();

        gate.Clear();
        gate.Accept("session-current").Should().BeFalse();
    }

    [Fact]
    public void SessionGate_SetSession_rejects_whitespace()
    {
        var gate = new SessionGate();
        var act = () => gate.SetSession("  ");
        act.Should().Throw<ArgumentException>();
    }
}
