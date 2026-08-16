import CoreMedia
import CoreVideo
import Foundation
import os
import VideoToolbox

/// Hardware H.264 encoder (VideoToolbox). Used to force Baseline/Main H.264
/// parameters and as a documented encode path; WebRTC also uses VT on iOS
/// when H.264 is selected in the SDP.
public final class H264Encoder {
    public struct EncodedNal {
        public let data: Data
        public let isKeyFrame: Bool
        public let pts: CMTime
    }

    public var onEncoded: ((EncodedNal) -> Void)?

    private var session: VTCompressionSession?
    private var width = 0
    private var height = 0
    private let bitrate: Int
    private let fps: Int32
    private let log = Logger(subsystem: "com.remotephone.video", category: "h264")

    public init(bitrate: Int = 1_500_000, fps: Int32 = 24) {
        self.bitrate = bitrate
        self.fps = fps
    }

    public func encode(_ pixelBuffer: CVPixelBuffer, presentationTime: CMTime) {
        let w = CVPixelBufferGetWidth(pixelBuffer)
        let h = CVPixelBufferGetHeight(pixelBuffer)
        if session == nil || width != w || height != h {
            resetSession(width: w, height: h)
        }
        guard let session else { return }
        var flags: VTEncodeInfoFlags = []
        VTCompressionSessionEncodeFrame(
            session,
            imageBuffer: pixelBuffer,
            presentationTimeStamp: presentationTime,
            duration: .invalid,
            frameProperties: nil,
            infoFlagsOut: &flags
        )
    }

    public func finish() {
        if let session {
            VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
            VTCompressionSessionInvalidate(session)
        }
        session = nil
    }

    private func resetSession(width: Int, height: Int) {
        finish()
        self.width = width
        self.height = height
        var session: VTCompressionSession?
        let status = VTCompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            width: Int32(width),
            height: Int32(height),
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: nil,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            outputCallback: compressionCallback,
            refcon: Unmanaged.passUnretained(self).toOpaque(),
            compressionSessionOut: &session
        )
        guard status == noErr, let session else {
            log.error("VTCompressionSessionCreate failed \(status, privacy: .public)")
            return
        }
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ProfileLevel, value: kVTProfileLevel_H264_Baseline_AutoLevel)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate, value: bitrate as CFNumber)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: fps as CFNumber)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: fps * 2 as CFNumber)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        VTCompressionSessionPrepareToEncodeFrames(session)
        self.session = session
        log.info("H.264 encoder \(width, privacy: .public)x\(height, privacy: .public)")
    }
}

private func compressionCallback(
    outputCallbackRefCon: UnsafeMutableRawPointer?,
    sourceFrameRefCon: UnsafeMutableRawPointer?,
    status: OSStatus,
    infoFlags: VTEncodeInfoFlags,
    sampleBuffer: CMSampleBuffer?
) {
    guard status == noErr, let sampleBuffer, let refCon = outputCallbackRefCon else { return }
    let encoder = Unmanaged<H264Encoder>.fromOpaque(refCon).takeUnretainedValue()
    guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[CFString: Any]],
          let dataBuffer = CMSampleBufferGetDataBuffer(sampleBuffer)
    else { return }
    let isKey = (attachments.first?[kCMSampleAttachmentKey_NotSync] as? Bool) != true
    var length = 0
    var dataPointer: UnsafeMutablePointer<Int8>?
    CMBlockBufferGetDataPointer(dataBuffer, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &length, dataPointerOut: &dataPointer)
    guard let dataPointer, length > 0 else { return }
    let data = Data(bytes: dataPointer, count: length)
    let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
    encoder.onEncoded?(H264Encoder.EncodedNal(data: data, isKeyFrame: isKey, pts: pts))
}
