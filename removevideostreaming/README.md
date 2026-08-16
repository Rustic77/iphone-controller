# RemotePhone Windows Agent

Windows-side **video** agent for a remote physical iPhone control platform. Captures an AirPlay receiver window (AirServer / Reflector) with Windows Graphics Capture, then (in later phases) streams that video over WebRTC to a browser.

## TWO INDEPENDENT PATHS (permanent rule)

Control and video must never be merged. Do not route video through the ESP32. Do not send HID through the video agent.

```
CONTROL PATH (existing — do not modify ESP32 firmware in this repo)
  Browser trackpad/UI
       │
       ▼
  controllerplatform (hub)
       │  outbound WSS
       ▼
  ESP32-S3  ──USB HID──▶  iPhone

VIDEO PATH (this project)
  iPhone
       │  AirPlay (Wi‑Fi or USB Personal Hotspot)
       ▼
  Built-in sidecar (AirPlay-Windows, GPL)  **or**  AirServer / Reflector
       │  HWND capture
       ▼
  RemotePhone Windows Agent
       │  WebRTC
       ▼
  Remote browser
```

| Path | Transport | Purpose |
|------|-----------|---------|
| Control | Browser → hub → ESP32 → USB HID | Mouse / keyboard / gestures on the phone |
| Video | Phone → AirPlay → receiver window → Agent → WebRTC | Live phone screen to the operator |

---

## Phase map

| Phase | Goal | Status focus |
|-------|------|--------------|
| **1** | Local AirPlay window detect + Graphics Capture preview | This runbook |
| **2** | SIPSorcery WebRTC + signaling + `docs/test-receiver.html` | After Phase 1 preview works |
| **3** | `controllerplatform` video status + embedded player | Separate WS populations |
| **4** | Calibrated normalized taps from video (trackpad remains) | Agent/server conversion preferred |
| **5** | Failure matrix, soak metrics, reconnect/backoff | Production hardening |

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for module layout and path diagrams.

---

## Prerequisites (Phase 1)

Exact environment:

| Requirement | Detail |
|-------------|--------|
| OS | **Windows 11** only |
| IDE | Visual Studio 2022 17.x+ **or** VS Code / Cursor + .NET CLI |
| Workload | Desktop development with C++ (for WinUI tooling) recommended |
| .NET SDK | **.NET 10** (`net10.0` / `net10.0-windows10.0.26100.0`) |
| UI stack | **WinUI 3** via **Windows App SDK 2.3.1** |
| AirPlay receiver | **Built-in** (agent downloads AirPlay-Windows, GPL-3) **or** AirServer / Reflector |
| Network | iPhone and PC on the same Wi‑Fi (AirPlay) |
| Hardware | Existing ESP32 HID path is independent; not required for Phase 1 local preview |

Not used in Phase 1: Mac, Xcode, custom iOS app, HDMI, capture card, SIPSorcery streaming UI (pins may already be present for Phase 2).

### NuGet pins (current)

From `src/RemotePhone.Agent/RemotePhone.Agent.csproj`:

| Package | Version |
|---------|---------|
| Microsoft.WindowsAppSDK | **2.3.1** |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2526 |
| Microsoft.Windows.SDK.BuildTools.WinApp | 0.5.0 |
| CommunityToolkit.Mvvm | 8.4.0 |
| Microsoft.Extensions.Configuration / Binder / Json / Logging* | 9.0.0 |
| SIPSorcery | **10.0.6** |
| SIPSorceryMedia.Abstractions | 10.0.11 |

Core (`RemotePhone.Agent.Core`) targets `net10.0` with Configuration / Logging abstractions 9.0.0.

Tests: xUnit 2.9.3, FluentAssertions 7.0.0, Microsoft.NET.Test.Sdk 17.14.1.

### Security advisory (SIPSorcery)

Package **SIPSorcery 10.0.6** is flagged with known high severity advisory **[GHSA-28gm-jrmw-xx93](https://github.com/advisories/GHSA-28gm-jrmw-xx93)**. Track upgrade to a fixed release before exposing WebRTC in production (Phase 2+). Do not ignore this when promoting beyond local lab use.

---

## Build and run

```powershell
cd c:\esp\removevideostreaming

# Restore + build Agent (WinUI / Windows App SDK)
dotnet build src\RemotePhone.Agent\RemotePhone.Agent.csproj -c Debug

# Run unpackaged (no Windows Developer Mode required).
# Default launch profile starts built-in AirPlay (--start-airplay).
dotnet run --project src\RemotePhone.Agent\RemotePhone.Agent.csproj -c Debug

# Unit tests (Core logic only — no Graphics.Capture)
dotnet test tests\RemotePhone.Agent.Tests\RemotePhone.Agent.Tests.csproj
```

Optional: open `RemotePhone.sln` in Visual Studio and F5 the `RemotePhone.Agent` startup project (x64 recommended).

---

## Built-in AirPlay (lab, no AirServer)

The agent can download and run **AirPlay-Windows** (GPL-3, UxPlay port) as a sidecar.

1. Start **RemotePhone.Agent** (`dotnet run` passes `--start-airplay` by default). First run fetches ~13 MB to `%LOCALAPPDATA%\RemotePhone\airplay-windows\`.
2. Or click **START BUILT-IN AIRPLAY** if you launched without that flag.
3. Allow the Windows firewall prompt if it appears.
4. On the iPhone: Control Center → Screen Mirroring → **AirPlay-Windows**.
5. The agent captures that window and can WebRTC it to the relay UI.

See [docs/THIRD_PARTY_NOTICES.md](docs/THIRD_PARTY_NOTICES.md). DRM apps (Netflix / YouTube / Apple TV+) will not mirror.

AirServer / Reflector still work via **SELECT WINDOW**.

---

## Phase 1 AirPlay procedure (external receiver)

### 1. Start the receiver

1. Launch **AirServer** or **Reflector** on the Windows PC.
2. Confirm the receiver is discoverable on the LAN (firewall allows AirPlay / Bonjour as required by the product).

### 2. Mirror the iPhone

1. On the iPhone: Control Center → **Screen Mirroring** (or AirPlay).
2. Select the AirServer / Reflector target.
3. Confirm the phone UI appears in the receiver window on Windows.

### 3. Select the receiver in the Agent

1. Start **RemotePhone.Agent**.
2. Refresh / open the receiver list (windows scored by process / exe / title: `AirServer`, `Reflector`, `AirPlay`).
3. Choose the correct HWND entry (**SELECT AIRPLAY WINDOW**).
4. Confirm process name, title, HWND, and dimensions look right.

### 4. Confirm capture

1. Start capture; live preview should show the mirrored phone UI (GPU path, no PNG/JPEG dump).
2. Check status fields: Capture Status, Resolution, Orientation (`height > width` → Portrait), FPS, Dropped Frames.
3. Diagnostics: app / .NET / Windows version, GPU, selected receiver, last error.
4. Close the receiver window once → Agent should move to **SourceLost** without crashing; restore mirror → resume when implemented.

### 5. Files created / project layout

```
c:\esp\removevideostreaming\
  README.md                          ← this runbook
  docs\
    ARCHITECTURE.md
    TROUBLESHOOTING.md
    PROTOCOL.md                      ← Phase 2+ signaling
    FAILURE_MODE_MATRIX.md           ← Phase 5 concepts
    SOAK_TEST.md
    test-receiver.html               ← Phase 2 browser smoke page
  src\
    RemotePhone.Agent\               ← WinUI agent
    RemotePhone.Agent.Core\          ← portable logic (tests target this)
  tests\
    RemotePhone.Agent.Tests\
```

---

## Known limitations (Phase 1)

- Local preview only until Phase 2 WebRTC is verified.
- Detection is heuristic (process / path / title); false positives/negatives possible.
- Built-in receiver is GPLv3 AirPlay-Windows (lab only); DRM apps do not mirror.
- Packaged WinUI apps need the correct capabilities (see [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) — `graphicsCapture`).
- GPU resets / exclusive fullscreen on the receiver can black the capture.
- SIPSorcery advisory GHSA-28gm-jrmw-xx93 applies when WebRTC packages are used.
- ESP32 / `iphone-controller` firmware is out of scope; do not change it from this repo.
- Do not send video over the ESP32 USB link.

---

## Docs index

| Doc | Contents |
|-----|----------|
| [THIRD_PARTY_NOTICES.md](docs/THIRD_PARTY_NOTICES.md) | AirPlay-Windows sidecar license / hash |
| [TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | Detection, black frames, capability, GPU |
| [PROTOCOL.md](docs/PROTOCOL.md) | Agent WebRTC signaling messages |
| [FAILURE_MODE_MATRIX.md](docs/FAILURE_MODE_MATRIX.md) | FailureModeCatalog table |
| [SOAK_TEST.md](docs/SOAK_TEST.md) | SoakMetrics usage |
| [test-receiver.html](docs/test-receiver.html) | Minimal browser WebRTC receiver |

Placeholder signaling URL for the test page: `ws://localhost:8080/ws/browser-video` (documented for Phase 2; not required for Phase 1 local preview).
