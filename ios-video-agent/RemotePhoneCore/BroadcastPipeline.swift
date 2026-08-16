import CoreMedia
import Foundation
import ImageIO
import os
import ReplayKit
import WebRTC

/// Orchestrates signaling + WebRTC inside the ReplayKit broadcast extension.
public final class BroadcastPipeline: NSObject, SignalingClientDelegate, WebRtcPublisherDelegate {
    private var config: AgentConfig?
    private var signaling: SignalingClient?
    private var publisher: WebRtcPublisher?
    private var sessionId: String?
    private var started = false
    private let log = Logger(subsystem: "com.remotephone.video", category: "pipeline")
    private let h264 = H264Encoder()

    public func start() throws {
        let config = try AgentConfig.load()
        self.config = config
        let signaling = SignalingClient(config: config)
        signaling.delegate = self
        self.signaling = signaling
        signaling.connect()
        log.info("pipeline starting for \(config.deviceId, privacy: .public)")
    }

    public func stop() {
        publisher?.stop()
        publisher = nil
        signaling?.send(AgentMessage.sourceLost("broadcast_finished"))
        signaling?.disconnect()
        signaling = nil
        sessionId = nil
        started = false
        h264.finish()
    }

    public func handleVideo(_ sampleBuffer: CMSampleBuffer) {
        var rotation = RTCVideoRotation._0
        var orientationName = "portrait"
        if let raw = CMGetAttachment(
            sampleBuffer,
            key: RPVideoSampleOrientationKey as CFString,
            attachmentModeOut: nil
        ) as? NSNumber {
            let mapped = Self.mapOrientation(raw.uint32Value)
            rotation = mapped.rotation
            orientationName = mapped.name
        }
        // Keep VideoToolbox warm with the same frames WebRTC publishes (H.264 path).
        if let pixel = CMSampleBufferGetImageBuffer(sampleBuffer) {
            h264.encode(pixel, presentationTime: CMSampleBufferGetPresentationTimeStamp(sampleBuffer))
        }
        publisher?.capture(sampleBuffer, rotation: rotation, orientationName: orientationName)
    }

    public func signalingDidOpen() {
        log.info("signaling open; waiting for stream_start")
        signaling?.send(AgentMessage.streamState("idle", detail: "waiting_for_stream_start"))
    }

    public func signalingDidReceive(_ body: [String: Any]) {
        guard let type = AgentMessage.type(of: body) else { return }
        switch type {
        case "registered":
            log.info("agent registered")
        case "stream_start":
            sessionId = body["sessionId"] as? String
            startPublisher()
        case "stream_stop":
            publisher?.stop()
            publisher = nil
            started = false
            signaling?.send(AgentMessage.streamState("idle"))
        case "webrtc_answer":
            if let sdp = body["sdp"] as? String {
                publisher?.setRemoteAnswer(sdp: sdp)
            }
        case "ice_candidate":
            if let candidate = body["candidate"] as? String {
                let mid = body["sdpMid"] as? String
                let idx = (body["sdpMLineIndex"] as? Int).map { Int32($0) } ?? 0
                publisher?.addIce(candidate: candidate, sdpMid: mid, sdpMLineIndex: idx)
            }
        case "heartbeat_ack":
            break
        case "error":
            let reason = body["reason"] as? String ?? "unknown"
            log.error("hub error: \(reason, privacy: .public)")
        default:
            break
        }
    }

    public func signalingDidFail(_ error: Error) {
        log.error("signaling failed: \(error.localizedDescription, privacy: .public)")
    }

    public func signalingDidClose() {
        log.info("signaling closed")
    }

    public func webRtcDidGenerateOffer(sdp: String) {
        guard let sessionId, let deviceId = config?.deviceId else { return }
        signaling?.send(AgentMessage.webrtcOffer(sessionId: sessionId, sdp: sdp, deviceId: deviceId))
        signaling?.send(AgentMessage.streamState("offering"))
    }

    public func webRtcDidGenerateIce(candidate: String, sdpMid: String?, sdpMLineIndex: Int32) {
        guard let sessionId, let deviceId = config?.deviceId else { return }
        signaling?.send(
            AgentMessage.iceCandidate(
                sessionId: sessionId,
                deviceId: deviceId,
                candidate: candidate,
                sdpMid: sdpMid,
                sdpMLineIndex: Int(sdpMLineIndex)
            )
        )
    }

    public func webRtcDidChangeState(_ state: String) {
        signaling?.send(AgentMessage.streamState(state))
    }

    private func startPublisher() {
        let publisher = WebRtcPublisher()
        publisher.delegate = self
        publisher.onMetadata = { [weak self] w, h, orientation in
            self?.signaling?.send(AgentMessage.videoMetadata(width: w, height: h, orientation: orientation, fps: 24))
        }
        self.publisher = publisher
        started = true
        publisher.startOffer()
    }

    private static func mapOrientation(_ value: UInt32) -> (rotation: RTCVideoRotation, name: String) {
        switch CGImagePropertyOrientation(rawValue: value) {
        case .up, .upMirrored:
            return (._0, "portrait")
        case .down, .downMirrored:
            return (._180, "portrait")
        case .left, .leftMirrored:
            return (._90, "landscape")
        case .right, .rightMirrored:
            return (._270, "landscape")
        default:
            return (._0, "portrait")
        }
    }
}
