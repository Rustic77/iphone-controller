# Remote Phone — iOS screen agent

Streams the iPhone screen to the control relay with ReplayKit. **HID (clicks and keyboard) stays on the ESP32 USB path.** This app is video only.

Apple does not allow silent full-device capture. System-wide pixels come from a [Broadcast Upload Extension](https://developer.apple.com/documentation/replaykit/rpbroadcastsamplehandler) the user starts with [Screen Recording](https://developer.apple.com/documentation/replaykit/rpsystembroadcastpickerview) / Control Center.

DRM apps stay black. That is an OS rule.

## Requirements

- Mac with Xcode 15+ (this repo can be edited on Windows; **builds only on a Mac**)
- Apple Developer signing (free team works for ~7 days)
- iOS 17+ device (Simulator cannot broadcast the real phone screen)
- Control relay running (`controllerplatform`, `/ws/agent`)
- Same `device_id` + secret as the ESP32

## Generate the Xcode project

```bash
brew install xcodegen
cd ios-video-agent
xcodegen generate
open RemotePhoneVideo.xcodeproj
```

In Xcode:

1. Select the **RemotePhoneVideo** target → Signing & Capabilities → your Team.
2. Repeat for **BroadcastExtension**.
3. Capabilities → **App Groups** → `group.com.remotephone.video` on **both** targets.
4. File → Packages → Resolve (WebRTC from [stasel/WebRTC](https://github.com/stasel/WebRTC)).
5. Run on a physical iPhone.

## Pair and broadcast

1. Open **Remote Phone**.
2. Relay URL, e.g. `ws://10.0.0.6:8080/ws/agent` (LAN) or `wss://relay.example.com/ws/agent`.
3. Device id + secret (same as ESP NVS / `devices.json`).
4. **Save pairing**.
5. Tap the ReplayKit record button in the app, or Control Center → Screen Recording → **Remote Phone**.
6. On the PC: open the relay, Control the device, **CALIBRATE POINTER**, tap on the live video.

## Architecture

```
iPhone screen
    → ReplayKit Broadcast Extension (SampleHandler)
        → FrameScaler (VideoToolbox pixel transfer, max long edge 1280)
        → H264Encoder (VTCompressionSession, kept warm)
        → WebRtcPublisher (H.264 sendonly peer)
        → WSS /ws/agent  (x-device-id, x-agent-id, x-agent-secret)
            → controllerplatform hub
                → browser <video> (click surface) + ESP HID
```

The extension process must upload itself: the main app is backgrounded while you use Safari/Settings/other apps.

Memory budget for broadcast extensions is tight (~50 MB). Resolution is capped; audio is not captured.

## Bundle IDs

| Target | Bundle ID |
|--------|-----------|
| App | `com.remotephone.video` |
| Broadcast extension | `com.remotephone.video.broadcast` |
| App Group | `group.com.remotephone.video` |

Change these to your team’s IDs before App Store / TestFlight. Keep the extension id as `{appId}.broadcast` so `RPSystemBroadcastPickerView.preferredExtension` matches.

## Security

- Secret is stored in the App Group `UserDefaults` (needed by the extension). It is never logged.
- Prefer `wss://` in production. `NSAllowsArbitraryLoads` is enabled only so LAN `ws://` works during lab setup.
