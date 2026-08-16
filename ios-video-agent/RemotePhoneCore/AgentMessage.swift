import Foundation

/// Wire types for `/ws/agent`. Canonical names are snake_case to match
/// `controllerplatform/src/types.ts`. Secrets never appear in these payloads.
public enum AgentMessage {
    public static func register() -> [String: Any] {
        ["type": "register", "capabilities": ["replaykit", "h264"]]
    }

    public static func webrtcOffer(sessionId: String, sdp: String, deviceId: String) -> [String: Any] {
        ["type": "webrtc_offer", "sessionId": sessionId, "sdp": sdp, "deviceId": deviceId]
    }

    public static func iceCandidate(
        sessionId: String,
        deviceId: String,
        candidate: String,
        sdpMid: String?,
        sdpMLineIndex: Int?
    ) -> [String: Any] {
        var body: [String: Any] = [
            "type": "ice_candidate",
            "sessionId": sessionId,
            "deviceId": deviceId,
            "candidate": candidate,
        ]
        if let sdpMid { body["sdpMid"] = sdpMid }
        if let sdpMLineIndex { body["sdpMLineIndex"] = sdpMLineIndex }
        return body
    }

    public static func videoMetadata(width: Int, height: Int, orientation: String, fps: Double) -> [String: Any] {
        [
            "type": "video_metadata",
            "width": width,
            "height": height,
            "orientation": orientation,
            "fps": fps,
        ]
    }

    public static func streamState(_ state: String, detail: String? = nil) -> [String: Any] {
        var body: [String: Any] = ["type": "stream_state", "state": state]
        if let detail { body["detail"] = detail }
        return body
    }

    public static func sourceLost(_ reason: String) -> [String: Any] {
        ["type": "source_lost", "reason": reason]
    }

    public static func heartbeat() -> [String: Any] {
        ["type": "heartbeat", "ts": Int(Date().timeIntervalSince1970 * 1000)]
    }

    public static func encode(_ body: [String: Any]) throws -> Data {
        try JSONSerialization.data(withJSONObject: body, options: [])
    }

    public static func decode(_ data: Data) -> [String: Any]? {
        (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    }

    public static func type(of body: [String: Any]) -> String? {
        body["type"] as? String
    }
}
