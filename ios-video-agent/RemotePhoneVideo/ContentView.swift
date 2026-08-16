import ReplayKit
import SwiftUI
import RemotePhoneCore

struct ContentView: View {
    @State private var relayURL = AgentConfig.snapshotForUI().relayURL
    @State private var deviceId = AgentConfig.snapshotForUI().deviceId
    @State private var agentId = AgentConfig.snapshotForUI().agentId
    @State private var secret = ""
    @State private var status = AgentConfig.snapshotForUI().hasSecret
        ? "Paired. Start Screen Recording and pick Remote Phone."
        : "Save pairing, then start broadcast."
    @State private var errorText = ""

    var body: some View {
        NavigationStack {
            Form {
                Section("Hub pairing") {
                    TextField("Relay URL", text: $relayURL)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .keyboardType(.URL)
                    TextField("Device id", text: $deviceId)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                    TextField("Agent id", text: $agentId)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                    SecureField("Device secret", text: $secret)
                    Button("Save pairing") { save() }
                }
                Section("Broadcast") {
                    Text(status)
                        .foregroundStyle(.secondary)
                    BroadcastPicker()
                        .frame(height: 44)
                    Text("Or: Control Center → Screen Recording → Remote Phone. HID clicks still come from the ESP32 USB clicker, not this app.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
                if !errorText.isEmpty {
                    Section {
                        Text(errorText).foregroundStyle(.red)
                    }
                }
            }
            .navigationTitle("Remote Phone")
        }
    }

    private func save() {
        errorText = ""
        do {
            try AgentConfig.save(relayURL: relayURL, deviceId: deviceId, secret: secret, agentId: agentId)
            secret = ""
            status = "Saved. Start Screen Recording and pick Remote Phone."
        } catch {
            errorText = error.localizedDescription
        }
    }
}

/// System Screen Recording picker. The only supported way to start a Broadcast
/// Upload Extension from the app (see RPSystemBroadcastPickerView).
struct BroadcastPicker: UIViewRepresentable {
    func makeUIView(context: Context) -> RPSystemBroadcastPickerView {
        let picker = RPSystemBroadcastPickerView(frame: CGRect(x: 0, y: 0, width: 44, height: 44))
        picker.preferredExtension = "com.remotephone.video.broadcast"
        picker.showsMicrophoneButton = false
        return picker
    }

    func updateUIView(_ uiView: RPSystemBroadcastPickerView, context: Context) {}
}

#Preview {
    ContentView()
}
