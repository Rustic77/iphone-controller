import CoreMedia
import ReplayKit
import RemotePhoneCore

final class SampleHandler: RPBroadcastSampleHandler {
    private var pipeline: BroadcastPipeline?

    override func broadcastStarted(withSetupInfo setupInfo: [String: NSObject]?) {
        let pipeline = BroadcastPipeline()
        do {
            try pipeline.start()
            self.pipeline = pipeline
        } catch {
            finishBroadcastWithError(error as NSError)
        }
    }

    override func processSampleBuffer(_ sampleBuffer: CMSampleBuffer, with sampleBufferType: RPSampleBufferType) {
        guard sampleBufferType == .video else { return }
        pipeline?.handleVideo(sampleBuffer)
    }

    override func broadcastFinished() {
        pipeline?.stop()
        pipeline = nil
    }
}
