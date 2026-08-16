import Foundation
import os

public protocol SignalingClientDelegate: AnyObject {
    func signalingDidOpen()
    func signalingDidReceive(_ body: [String: Any])
    func signalingDidFail(_ error: Error)
    func signalingDidClose()
}

/// Outbound WebSocket to the hub `/ws/agent` endpoint.
/// Auth is handshake headers only; the secret is never logged.
public final class SignalingClient: NSObject, URLSessionWebSocketDelegate {
    public weak var delegate: SignalingClientDelegate?

    private let config: AgentConfig
    private var session: URLSession?
    private var task: URLSessionWebSocketTask?
    private let log = Logger(subsystem: "com.remotephone.video", category: "signaling")
    private var pingTimer: Timer?
    private let queue = DispatchQueue(label: "com.remotephone.video.signaling")

    public init(config: AgentConfig) {
        self.config = config
        super.init()
    }

    public func connect() {
        queue.async { [weak self] in
            self?.connectLocked()
        }
    }

    public func send(_ body: [String: Any]) {
        queue.async { [weak self] in
            guard let self, let task = self.task else { return }
            do {
                let data = try AgentMessage.encode(body)
                let text = String(data: data, encoding: .utf8) ?? ""
                task.send(.string(text)) { error in
                    if let error {
                        self.log.error("send failed: \(error.localizedDescription, privacy: .public)")
                    }
                }
            } catch {
                self.log.error("encode failed: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    public func disconnect() {
        queue.async { [weak self] in
            self?.pingTimer?.invalidate()
            self?.pingTimer = nil
            self?.task?.cancel(with: .goingAway, reason: nil)
            self?.task = nil
            self?.session?.invalidateAndCancel()
            self?.session = nil
        }
    }

    private func connectLocked() {
        var request = URLRequest(url: config.relayURL)
        request.timeoutInterval = 15
        request.setValue(config.deviceId, forHTTPHeaderField: "x-device-id")
        request.setValue(config.agentId, forHTTPHeaderField: "x-agent-id")
        request.setValue(config.secret, forHTTPHeaderField: "x-agent-secret")
        log.info("connecting to \(self.config.relayURL.host ?? "?", privacy: .public) as \(self.config.deviceId, privacy: .public)")

        let session = URLSession(configuration: .ephemeral, delegate: self, delegateQueue: nil)
        self.session = session
        let task = session.webSocketTask(with: request)
        self.task = task
        task.resume()
        listen()
        DispatchQueue.main.async { [weak self] in
            self?.pingTimer = Timer.scheduledTimer(withTimeInterval: 10, repeats: true) { [weak self] _ in
                self?.send(AgentMessage.heartbeat())
            }
        }
    }

    private func listen() {
        task?.receive { [weak self] result in
            guard let self else { return }
            switch result {
            case .failure(let error):
                self.log.error("receive failed: \(error.localizedDescription, privacy: .public)")
                DispatchQueue.main.async { self.delegate?.signalingDidFail(error) }
            case .success(let message):
                let data: Data?
                switch message {
                case .data(let d): data = d
                case .string(let s): data = s.data(using: .utf8)
                @unknown default: data = nil
                }
                if let data, let body = AgentMessage.decode(data) {
                    DispatchQueue.main.async { self.delegate?.signalingDidReceive(body) }
                }
                self.listen()
            }
        }
    }

    public func urlSession(
        _ session: URLSession,
        webSocketTask: URLSessionWebSocketTask,
        didOpenWithProtocol protocol: String?
    ) {
        log.info("signaling socket open")
        send(AgentMessage.register())
        DispatchQueue.main.async { self.delegate?.signalingDidOpen() }
    }

    public func urlSession(
        _ session: URLSession,
        webSocketTask: URLSessionWebSocketTask,
        didCloseWith closeCode: URLSessionWebSocketTask.CloseCode,
        reason: Data?
    ) {
        log.info("signaling socket closed code=\(closeCode.rawValue, privacy: .public)")
        DispatchQueue.main.async { self.delegate?.signalingDidClose() }
    }
}
