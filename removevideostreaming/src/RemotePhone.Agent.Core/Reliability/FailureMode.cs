namespace RemotePhone.Agent.Core.Reliability;

public sealed record FailureMode(
    string Trigger,
    string ExpectedBehavior,
    string AutomaticProtection,
    string RecoveryProcedure,
    string PassCondition);

public static class FailureModeCatalog
{
    public static IReadOnlyList<FailureMode> All() =>
    [
        new(
            Trigger: "AirPlay receiver window closed or process exited",
            ExpectedBehavior: "Capture transitions Capturing -> SourceLost; streaming pauses cleanly",
            AutomaticProtection: "SourceLost signaling message; frame queue cleared; no stale frames pushed",
            RecoveryProcedure: "Re-detect receiver window; transition SourceLost -> Capturing on restore",
            PassCondition: "Stream resumes within reconnect budget without agent restart"),

        new(
            Trigger: "AirPlay source resolution or orientation change mid-session",
            ExpectedBehavior: "VideoMetadata republished; orientation helper updates Portrait/Landscape",
            AutomaticProtection: "Drop in-flight frames with mismatched dimensions; recalibrate pointer scales",
            RecoveryProcedure: "Invalidate pointer calibration; send updated VideoMetadata; resume capture",
            PassCondition: "Client receives new metadata and taps map correctly after recalibration"),

        new(
            Trigger: "Capture FPS collapse / producer overrun",
            ExpectedBehavior: "BoundedFrameQueue drops oldest frames; DroppedFrames increments",
            AutomaticProtection: "Queue capacity default 3 prevents unbounded memory growth",
            RecoveryProcedure: "Lower PreferredFps/bitrate; monitor SoakMetrics.DroppedFrames",
            PassCondition: "Memory stable under soak; dropped frames reported; stream remains connected"),

        new(
            Trigger: "Capture pipeline error (GraphicsCapture failure)",
            ExpectedBehavior: "State machine Capturing -> Error; Error signaling message emitted",
            AutomaticProtection: "Stop push-frame path; dispose capture resources",
            RecoveryProcedure: "Error -> Idle|Selecting; operator reselects source; restart capture",
            PassCondition: "Agent returns to Idle/Selecting and can start a new session"),

        new(
            Trigger: "WebRTC peer connection ICE failure",
            ExpectedBehavior: "ConnectionState -> Failed or Reconnecting; ReconnectAsync invoked",
            AutomaticProtection: "ExponentialBackoff between reconnect attempts; SessionGate rejects stale offers",
            RecoveryProcedure: "ReconnectAsync with fresh offer/answer; reuse session only if gate accepts",
            PassCondition: "Media path restored or clean Failed state with Error message to client"),

        new(
            Trigger: "WebRTC media path stalls (zero FPS while Connected)",
            ExpectedBehavior: "NotifyFrame stats show StreamFps collapse; health watchdogs fire",
            AutomaticProtection: "Heartbeat continues; optional soft reconnect without tearing down capture",
            RecoveryProcedure: "ReconnectAsync; if still stalled, StreamStop then StreamStart",
            PassCondition: "StreamFps recovers above threshold within soak window"),

        new(
            Trigger: "Signaling WebSocket disconnect",
            ExpectedBehavior: "ISignalingClient.IsConnected false; reconnect with backoff",
            AutomaticProtection: "ExponentialBackoff; AgentRegister re-auth; reject messages until authenticated",
            RecoveryProcedure: "ConnectAsync; AgentRegister; resume Heartbeat; renegotiate WebRTC if needed",
            PassCondition: "Signaling restored; HeartbeatAck received; stream session continuity preserved or clean restart"),

        new(
            Trigger: "Stale session messages after StreamStop / new StreamStart",
            ExpectedBehavior: "SessionGate.Accept returns false for old sessionId",
            AutomaticProtection: "Ignore WebrtcOffer/Answer/IceCandidate for non-current session",
            RecoveryProcedure: "SetSession on StreamStart; Clear on StreamStop",
            PassCondition: "No cross-session ICE/SDP applied; only current session media established"),

        new(
            Trigger: "Agent authentication failure",
            ExpectedBehavior: "AgentAuthenticated Success=false; streaming never starts",
            AutomaticProtection: "Do not open capture or WebRTC until authenticated",
            RecoveryProcedure: "Refresh AgentCredential; reconnect and re-register",
            PassCondition: "Unauthenticated agent never pushes frames or accepts StreamStart"),

        new(
            Trigger: "TURN/STUN unreachable / restrictive NAT",
            ExpectedBehavior: "Connection may stay Connecting then Failed; ICE candidates exhaust",
            AutomaticProtection: "Configured TurnServers fallback; timeout to Failed",
            RecoveryProcedure: "Verify StunServers/TurnServers in Agent options; retry ReconnectAsync",
            PassCondition: "With valid TURN, Connected achieved; without, clear Failed + Error to client"),

        new(
            Trigger: "Pointer calibration invalidated by orientation change",
            ExpectedBehavior: "CalibrationState -> INVALID; taps withheld or remapped after recalibration",
            AutomaticProtection: "Invalidate() bumps Version; HomingPlan re-establishes origin",
            RecoveryProcedure: "BeginCalibrate; HomingPlan sweeps; MarkReady; apply ScaleX/ScaleY",
            PassCondition: "NormalizedToEstimatedPixel within tolerance after READY"),

        new(
            Trigger: "Long soak memory / reconnect pressure",
            ExpectedBehavior: "SoakMetrics tracks Runtime, DroppedFrames, ReconnectCount, MemoryBytes",
            AutomaticProtection: "Bounded queue; backoff caps reconnect storms",
            RecoveryProcedure: "Alert on ErrorCount/MemoryBytes thresholds; recycle agent process if needed",
            PassCondition: "Multi-hour soak meets PassCondition thresholds without unbounded growth"),
    ];
}
