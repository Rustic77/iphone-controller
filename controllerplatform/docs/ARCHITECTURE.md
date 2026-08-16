# Architecture

## Goal

Let an authenticated operator control a USB-HID iPhone rig (driven by an
ESP32-S3) from a browser, over the Internet, **without exposing the ESP32** to
inbound connections or requiring router port forwarding.

The operator UI is **HID only** (trackpad, clicks, keys). A Windows video agent
may still connect for local AirPlay capture, but the control panel does not
subscribe to or display a live stream.

## Topology

```
   Browser (operator UI)
      │  HTTPS + WSS  (session token)
      ▼
┌─────────────────────────────────┐
│   Cloud control server          │   ← this repo
│                                 │
│   • Fastify (HTTP + static)     │
│   • ws (WebSocket relay)        │
│   • Hub (routing + policy)      │
│   • PointerCalibration          │
└─────────────────────────────────┘
      ▲                    ▲
      │ CONTROL            │ VIDEO
      │ outbound WSS       │ outbound WS/WSS
      │ (device secret)    │ (device secret + agent id)
      │                    │
   ESP32-S3             Windows video agent
      │  USB HID            │  AirPlay window capture
      ▼                     │  WebRTC media
   iPhone  ◄── AirPlay ─────┘
```

Key properties:

1. **The ESP32 initiates the connection outward** to the server (CONTROL).
2. **The Windows agent initiates outward** to `/ws/agent` (VIDEO).
3. **CONTROL and VIDEO never share a transport.** HID commands never go through
   the agent; media never goes through the ESP. Either path can be up while the
   other is down.

## Components

### Fastify HTTP layer (`src/server.ts`)
- Serves the static web UI from `public/`.
- `POST /api/login` — operator login, returns a signed session token.
- `GET /api/health` — liveness probe.
- Provides the underlying Node HTTP server that the WebSocket layer attaches to.
- Emits structured JSON logs (pino).

### WebSocket layer (`src/server.ts`)
Three logical endpoints, authenticated **at the HTTP upgrade**:

- `/ws/browser?token=…` — operator sockets. Token verified by `SessionManager`.
- `/ws/device` — ESP sockets. `x-device-id` + `x-device-secret`.
- `/ws/agent` — Windows video agent. `x-device-id` + `x-agent-secret`
  (verified via `deviceStore.verifyDevice`) + `x-agent-id`.

All are `ws` servers in `noServer` mode; a single `upgrade` handler routes by
path, authenticates, and only then calls `handleUpgrade`. Failed auth ⇒ `401`.

Agent message types are normalized from PascalCase aliases to snake_case so the
existing Windows agent and the browser UI share one hub.

### The Hub (`src/hub.ts`)
The relay core and policy engine. Transport-agnostic via `Transport`
(`send` / `close`). Responsibilities:

- Track connected ESP devices (online/offline, last-seen, current controller).
- Track connected video agents per `deviceId` (`streaming`, `webRtcConnected`,
  `videoSessionId`).
- Track browser clients (HID claim; video subscribe is optional and unused by
  the controller UI).
- Enforce tenant isolation and single-controller rules.
- Route HID input to the claimed ESP only.
- Relay WebRTC signaling **only** between an owner browser (claimed or
  video-subscribed) and the agent for the **same** `deviceId`; reject wrong
  device and stale `sessionId`.
- Fan out device-list / video-status updates to owning browsers.
- Audit-log control/video session and connect/disconnect events (no secrets).

### Pointer calibration (`src/pointerCalibration.ts`)
Relative-HID planner used for video taps:

- Defaults to 1179×2556 portrait; landscape swaps axes.
- `calibrate_pointer` → many `move(-40,-40)` → estimated `(0,0)` → `READY`.
- `tap_normalized` → chunked relative moves + left click down/up.
- States: `UNCALIBRATED | CALIBRATING | READY | INVALID`.
- Orientation metadata from the agent invalidates calibration.

### Sequence tracker (`src/sequenceTracker.ts`)
Per-control-session gate that drops duplicate/out-of-order and stale browser
commands. The hub maintains a separate monotonic **device outbound seq** so
synthetic calibrate/tap batches never collide with browser wire seqs.

### Credential model
See [Credential model](#credential-model).

## Security invariants

1. **No unauthenticated control or video.** Every WebSocket upgrade is
   authenticated before the socket is accepted.
2. **Tenant isolation.** A browser may only list, claim, subscribe, or signal
   devices whose `ownerId` equals its `userId`.
3. **Routed, not broadcast.** HID input goes only to the claimed device.
   WebRTC signaling goes only to the matching device's agent / subscribed
   browsers.
4. **Single controller.** A device is controlled by at most one client at a
   time; a second claim gets `busy`. Video subscribe is per-browser and does
   not grant HID control.
5. **Fail safe on disconnect.** Controlling browser drop ⇒ ESP `release_all`.
   Agent drop ⇒ video_status offline for watchers; control claim is unaffected.
6. **Stale video sessions.** WebRTC messages with a non-current `sessionId`
   are rejected.

## Lifecycle flows

### Device (ESP) comes online
```
ESP ──WSS──▶ /ws/device (id+secret)
server: verifyDevice → Hub.addDevice → {hello}
server → owner's browsers: {devices} (controllerOnline=true)
```

### Video agent comes online
```
Agent ──WS──▶ /ws/agent (id+agent-id+secret)
server: verifyDevice → Hub.addVideoAgent → {registered}
server → owner's browsers: {devices} (videoAgentOnline=true)
```

### Operator claims HID
```
browser → {claim, deviceId}
Hub: owner? ESP online? free? → control session → {claimed}
browser → {input} → ESP HID (trackpad / clicks / keys)
```

Claim does **not** auto-subscribe video. The controller page never sends
`video_subscribe`.

### Independence
```
ESP disconnect  → controllerOnline=false; video can keep streaming
Agent disconnect → videoAgentOnline=false; HID claim can keep working
```

## Credential model

Intentionally minimal and swappable behind two interfaces:

| Concern            | Interface                       | MVP implementation                     | Replace with                          |
| ------------------ | ------------------------------- | -------------------------------------- | ------------------------------------- |
| Operator identity  | `UserStore`                     | `DevUserStore` (env `DEV_USERNAME/PW`) | DB users + hashed passwords / OAuth   |
| Browser sessions   | `SessionManager`                | HMAC-signed token + in-memory table    | JWT / server-side session store       |
| Device identity    | `DeviceCredentialStore`         | `InMemoryDeviceStore` (JSON seed file) | DB-backed provisioning + rotation     |

The Windows agent reuses the device secret (`x-agent-secret`); no separate
agent credential store in the MVP.

## Deliberate non-goals

- **Merging CONTROL and VIDEO** — permanently out of scope; paths stay independent.
- **Horizontal scale** — state is in-process. Multi-instance needs a shared bus
  keyed by device id.
- **Persistent history** — device/session state is in-memory only.

## Scaling note

Because routing is by in-memory maps, the MVP is single-instance. The Hub's
transport abstraction makes the natural next step clear: replace direct
`transport.send` with a publish to a message bus that the owning instance
consumes. No policy logic needs to change.
