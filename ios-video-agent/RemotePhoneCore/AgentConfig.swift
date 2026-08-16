import Foundation

/// Shared App Group keys. The pairing app writes; the broadcast extension reads.
/// The device secret is stored here so the extension can authenticate to `/ws/agent`.
/// It is never logged.
public struct AgentConfig {
    public static let appGroupId = "group.com.remotephone.video"
    public static let defaultAgentId = "ios-agent-01"

    public static let relayURLKey = "relayURL"
    public static let deviceIdKey = "deviceId"
    public static let secretKey = "deviceSecret"
    public static let agentIdKey = "agentId"

    public var relayURL: URL
    public var deviceId: String
    public var secret: String
    public var agentId: String

    public init(relayURL: URL, deviceId: String, secret: String, agentId: String = AgentConfig.defaultAgentId) {
        self.relayURL = relayURL
        self.deviceId = deviceId
        self.secret = secret
        self.agentId = agentId
    }

    private static func defaults() -> UserDefaults? {
        UserDefaults(suiteName: appGroupId)
    }

    public static func load() throws -> AgentConfig {
        guard let defaults = defaults() else {
            throw AgentConfigError.appGroupUnavailable
        }
        guard let urlString = defaults.string(forKey: relayURLKey)?.trimmingCharacters(in: .whitespacesAndNewlines),
              !urlString.isEmpty,
              let url = URL(string: urlString)
        else {
            throw AgentConfigError.missingRelayURL
        }
        guard url.scheme == "ws" || url.scheme == "wss" else {
            throw AgentConfigError.invalidRelayURL
        }
        let deviceId = (defaults.string(forKey: deviceIdKey) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        let secret = defaults.string(forKey: secretKey) ?? ""
        guard !deviceId.isEmpty else { throw AgentConfigError.missingDeviceId }
        guard !secret.isEmpty else { throw AgentConfigError.missingSecret }
        let agentId = (defaults.string(forKey: agentIdKey) ?? defaultAgentId).trimmingCharacters(in: .whitespacesAndNewlines)
        return AgentConfig(
            relayURL: url,
            deviceId: deviceId,
            secret: secret,
            agentId: agentId.isEmpty ? defaultAgentId : agentId
        )
    }

    public static func save(relayURL: String, deviceId: String, secret: String, agentId: String = defaultAgentId) throws {
        guard let defaults = defaults() else {
            throw AgentConfigError.appGroupUnavailable
        }
        let url = relayURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let parsed = URL(string: url), parsed.scheme == "ws" || parsed.scheme == "wss" else {
            throw AgentConfigError.invalidRelayURL
        }
        let id = deviceId.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !id.isEmpty else { throw AgentConfigError.missingDeviceId }
        guard !secret.isEmpty else { throw AgentConfigError.missingSecret }
        defaults.set(url, forKey: relayURLKey)
        defaults.set(id, forKey: deviceIdKey)
        defaults.set(secret, forKey: secretKey)
        defaults.set(agentId.trimmingCharacters(in: .whitespacesAndNewlines), forKey: agentIdKey)
        defaults.synchronize()
    }

    public static func snapshotForUI() -> (relayURL: String, deviceId: String, agentId: String, hasSecret: Bool) {
        let defaults = defaults()
        return (
            defaults?.string(forKey: relayURLKey) ?? "ws://10.0.0.6:8080/ws/agent",
            defaults?.string(forKey: deviceIdKey) ?? "esp32-lab-01",
            defaults?.string(forKey: agentIdKey) ?? defaultAgentId,
            !(defaults?.string(forKey: secretKey) ?? "").isEmpty
        )
    }
}

public enum AgentConfigError: Error, LocalizedError {
    case appGroupUnavailable
    case missingRelayURL
    case invalidRelayURL
    case missingDeviceId
    case missingSecret

    public var errorDescription: String? {
        switch self {
        case .appGroupUnavailable: return "App Group \(AgentConfig.appGroupId) is not available. Enable it on both targets in Xcode."
        case .missingRelayURL: return "Relay URL is missing. Save pairing in the app first."
        case .invalidRelayURL: return "Relay URL must be ws:// or wss:// and include /ws/agent."
        case .missingDeviceId: return "Device id is required."
        case .missingSecret: return "Device secret is required."
        }
    }
}
