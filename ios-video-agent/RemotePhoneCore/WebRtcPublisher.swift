import CoreMedia
import CoreVideo
import Foundation
import os
import WebRTC

public protocol WebRtcPublisherDelegate: AnyObject {
    func webRtcDidGenerateOffer(sdp: String)
    func webRtcDidGenerateIce(candidate: String, sdpMid: String?, sdpMLineIndex: Int32)
    func webRtcDidChangeState(_ state: String)
}

/// Publishes ReplayKit frames on a sendonly WebRTC peer connection using H.264
/// (VideoToolbox encoder inside Google WebRTC on iOS).
public final class WebRtcPublisher: NSObject, RTCPeerConnectionDelegate {
    public weak var delegate: WebRtcPublisherDelegate?

    private let factory: RTCPeerConnectionFactory
    private let videoSource: RTCVideoSource
    private let videoTrack: RTCVideoTrack
    private let capturer: RTCVideoCapturer
    private var peerConnection: RTCPeerConnection?
    private let log = Logger(subsystem: "com.remotephone.video", category: "webrtc")
    private let scaler = FrameScaler(maxLongEdge: 1280)
    private var lastWidth = 0
    private var lastHeight = 0
    private var lastMetadataAt = Date.distantPast
    public var onMetadata: ((Int, Int, String) -> Void)?

    public override init() {
        RTCInitializeSSL()
        let encoder = RTCDefaultVideoEncoderFactory()
        encoder.preferredCodec = RTCVideoCodecInfo(name: "H264")
        let decoder = RTCDefaultVideoDecoderFactory()
        factory = RTCPeerConnectionFactory(encoderFactory: encoder, decoderFactory: decoder)
        videoSource = factory.videoSource()
        videoTrack = factory.videoTrack(with: videoSource, trackId: "video0")
        capturer = RTCVideoCapturer(delegate: videoSource)
        super.init()
    }

    public func startOffer() {
        teardownPeer()
        let config = RTCConfiguration()
        config.sdpSemantics = .unifiedPlan
        config.iceServers = [RTCIceServer(urlStrings: ["stun:stun.l.google.com:19302"])]
        config.continualGatheringPolicy = .gatherContinually
        let constraints = RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: ["DtlsSrtpKeyAgreement": "true"])
        guard let pc = factory.peerConnection(with: config, constraints: constraints, delegate: self) else {
            log.error("failed to create peer connection")
            return
        }
        peerConnection = pc
        let initInit = RTCRtpTransceiverInit()
        initInit.direction = .sendOnly
        pc.addTransceiver(with: videoTrack, init: initInit)

        let offerConstraints = RTCMediaConstraints(
            mandatoryConstraints: [
                "OfferToReceiveAudio": "false",
                "OfferToReceiveVideo": "false",
            ],
            optionalConstraints: nil
        )
        pc.offer(for: offerConstraints) { [weak self] sdp, error in
            guard let self else { return }
            if let error {
                self.log.error("offer failed: \(error.localizedDescription, privacy: .public)")
                return
            }
            guard let sdp else { return }
            let preferred = Self.preferH264(sdp)
            pc.setLocalDescription(preferred) { error in
                if let error {
                    self.log.error("setLocalDescription failed: \(error.localizedDescription, privacy: .public)")
                    return
                }
                self.delegate?.webRtcDidGenerateOffer(sdp: preferred.sdp)
            }
        }
    }

    public func setRemoteAnswer(sdp: String) {
        let desc = RTCSessionDescription(type: .answer, sdp: sdp)
        peerConnection?.setRemoteDescription(desc) { [weak self] error in
            if let error {
                self?.log.error("setRemoteDescription failed: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    public func addIce(candidate: String, sdpMid: String?, sdpMLineIndex: Int32) {
        let ice = RTCIceCandidate(sdp: candidate, sdpMLineIndex: sdpMLineIndex, sdpMid: sdpMid)
        peerConnection?.add(ice)
    }

    public func capture(_ sampleBuffer: CMSampleBuffer, rotation: RTCVideoRotation, orientationName: String) {
        guard let scaled = scaler.scale(sampleBuffer) else { return }
        let w = CVPixelBufferGetWidth(scaled)
        let h = CVPixelBufferGetHeight(scaled)
        if w != lastWidth || h != lastHeight || Date().timeIntervalSince(lastMetadataAt) > 2 {
            lastWidth = w
            lastHeight = h
            lastMetadataAt = Date()
            onMetadata?(w, h, orientationName)
        }
        let ts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let ns = Int64(CMTimeGetSeconds(ts) * Double(NSEC_PER_SEC))
        let rtcBuffer = RTCCVPixelBuffer(pixelBuffer: scaled)
        let frame = RTCVideoFrame(buffer: rtcBuffer, rotation: rotation, timeStampNs: ns)
        videoSource.capturer(capturer, didCapture: frame)
    }

    public func stop() {
        teardownPeer()
    }

    private func teardownPeer() {
        peerConnection?.close()
        peerConnection = nil
    }

    public func peerConnection(_ peerConnection: RTCPeerConnection, didChange stateChanged: RTCSignalingState) {}
    public func peerConnection(_ peerConnection: RTCPeerConnection, didAdd stream: RTCMediaStream) {}
    public func peerConnection(_ peerConnection: RTCPeerConnection, didRemove stream: RTCMediaStream) {}
    public func peerConnectionShouldNegotiate(_ peerConnection: RTCPeerConnection) {}
    public func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceConnectionState) {
        delegate?.webRtcDidChangeState("ice-\(newState.rawValue)")
    }
    public func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceGatheringState) {}
    public func peerConnection(_ peerConnection: RTCPeerConnection, didGenerate candidate: RTCIceCandidate) {
        delegate?.webRtcDidGenerateIce(
            candidate: candidate.sdp,
            sdpMid: candidate.sdpMid,
            sdpMLineIndex: candidate.sdpMLineIndex
        )
    }
    public func peerConnection(_ peerConnection: RTCPeerConnection, didRemove candidates: [RTCIceCandidate]) {}
    public func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCPeerConnectionState) {
        let label: String
        switch newState {
        case .new: label = "new"
        case .connecting: label = "connecting"
        case .connected: label = "connected"
        case .disconnected: label = "disconnected"
        case .failed: label = "failed"
        case .closed: label = "closed"
        @unknown default: label = "pc-\(newState.rawValue)"
        }
        delegate?.webRtcDidChangeState(label)
    }

    private static func preferH264(_ sdp: RTCSessionDescription) -> RTCSessionDescription {
        let lines = sdp.sdp.components(separatedBy: "\r\n")
        var h264Pt: String?
        for line in lines where line.hasPrefix("a=rtpmap:") && line.lowercased().contains("h264") {
            h264Pt = line.dropFirst("a=rtpmap:".count).split(separator: " ").first.map(String.init)
            break
        }
        guard let h264Pt else { return sdp }
        let rewritten = lines.map { line -> String in
            if line.hasPrefix("m=video") {
                let parts = line.split(separator: " ").map(String.init)
                guard parts.count > 3 else { return line }
                let header = parts[0...2]
                let pts = parts[3...]
                let ordered = [h264Pt] + pts.filter { $0 != h264Pt }
                return (Array(header) + ordered).joined(separator: " ")
            }
            return line
        }.joined(separator: "\r\n")
        return RTCSessionDescription(type: sdp.type, sdp: rewritten)
    }
}
