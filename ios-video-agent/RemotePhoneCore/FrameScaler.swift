import CoreMedia
import CoreVideo
import Foundation
import VideoToolbox

/// Downscales ReplayKit pixel buffers so the broadcast extension stays under
/// its ~50 MB memory budget. Uses VideoToolbox's pixel transfer session.
public final class FrameScaler {
    public let maxLongEdge: Int
    private var session: VTPixelTransferSession?
    private var outputPool: CVPixelBufferPool?
    private var poolWidth = 0
    private var poolHeight = 0

    public init(maxLongEdge: Int = 1280) {
        self.maxLongEdge = max(320, maxLongEdge)
        var s: VTPixelTransferSession?
        let status = VTPixelTransferSessionCreate(allocator: kCFAllocatorDefault, pixelTransferSessionOut: &s)
        if status == noErr {
            session = s
        }
    }

    deinit {
        if let session {
            VTPixelTransferSessionInvalidate(session)
        }
    }

    public func scale(_ sampleBuffer: CMSampleBuffer) -> CVPixelBuffer? {
        guard let src = CMSampleBufferGetImageBuffer(sampleBuffer) else { return nil }
        let srcW = CVPixelBufferGetWidth(src)
        let srcH = CVPixelBufferGetHeight(src)
        let longEdge = max(srcW, srcH)
        if longEdge <= maxLongEdge {
            return src
        }
        let scale = Double(maxLongEdge) / Double(longEdge)
        let dstW = max(2, (Int(Double(srcW) * scale) / 2) * 2)
        let dstH = max(2, (Int(Double(srcH) * scale) / 2) * 2)
        guard let dst = makeOutputBuffer(width: dstW, height: dstH) else { return src }
        guard let session else { return src }
        let status = VTPixelTransferSessionTransferImage(session, from: src, to: dst)
        return status == noErr ? dst : src
    }

    private func makeOutputBuffer(width: Int, height: Int) -> CVPixelBuffer? {
        if outputPool == nil || poolWidth != width || poolHeight != height {
            poolWidth = width
            poolHeight = height
            let attrs: [CFString: Any] = [
                kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_420YpCbCr8BiPlanarFullRange,
                kCVPixelBufferWidthKey: width,
                kCVPixelBufferHeightKey: height,
                kCVPixelBufferIOSurfacePropertiesKey: [:] as CFDictionary,
            ]
            var pool: CVPixelBufferPool?
            CVPixelBufferPoolCreate(kCFAllocatorDefault, nil, attrs as CFDictionary, &pool)
            outputPool = pool
        }
        var buffer: CVPixelBuffer?
        guard let outputPool else { return nil }
        CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault, outputPool, &buffer)
        return buffer
    }
}
