# Wire Protocol

Transport: **WebSocket**. Every frame is a single JSON object with a `type`
field (a discriminated union). Definitions live in
[`src/types.ts`](../src/types.ts) — that file is the source of truth.

There are **three** independent socket populations:

- **Browser** operator sockets on `/ws/browser`
- **Device** (ESP32) sockets on `/ws/device` — **CONTROL** path
- **Agent** (Windows video) sockets on `/ws/agent` — **VIDEO** path

**CONTROL and VIDEO are independent.** An ESP offline does not imply the video
agent is offline, and vice versa. The server never trusts a socket to name
itself; identity is established at authentication (below), not from message
fields.

---

## 1. Authentication (HTTP upgrade)

Authentication happens during the WebSocket **upgrade request**, before the
socket is accepted. A failure returns HTTP `401` and the connection is dropped.

### Operator login (REST, precedes the browser socket)

```
POST /api/login
Content-Type: application/json

{ "username": "admin", "password": "…" }
```

Response `200`:

```json
{ "token": "<payload>.<sig>", "userId": "dev-operator", "expiresAt": 1786500000000 }
```

`401` on bad credentials.

### Browser socket

```
GET /ws/browser?token=<token>
```

The `token` is the value from `/api/login`. Invalid/expired/revoked ⇒ `401`.

### Device socket (CONTROL)

```
GET /ws/device
x-device-id:     <deviceId>
x-device-secret: <secret>
```

(For browser-based testing, `?deviceId=…&secret=…` query params are also
accepted.) Unknown id or wrong secret ⇒ `401`.

### Agent socket (VIDEO)

```
GET /ws/agent
x-device-id:     <deviceId>
x-agent-id:      <agentId>
x-agent-secret:  <device secret>
```

The agent authenticates with the **same per-device secret** as the ESP
(`deviceStore.verifyDevice`). The `x-agent-id` identifies the Windows agent
instance. Failed auth ⇒ `401`.

---

## 2. Input events

The atomic unit of control. Produced by the operator UI, relayed to the
device inside an `input` message.

```ts
type MouseButton = "left" | "right" | "middle";

type InputEvent =
  | { kind: "move";   dx: number; dy: number }          // relative pointer move
  | { kind: "click";  button: MouseButton; pressed: boolean } // down (true) / up (false)
  | { kind: "scroll"; dx: number; dy: number }          // wheel ticks; +dy = up
  | { kind: "text";   text: string }                    // type a UTF-8 string
  | { kind: "key";    code: string; pressed: boolean }  // named key down/up (e.g. "Enter")
  | { kind: "release_all" };                            // drop all buttons/keys
```

A **drag** is expressed as `click(pressed:true)` → one or more `move` → `click(pressed:false)`.

---

## 3. Browser → Server

```ts
type BrowserToServer =
  | { type: "claim";  deviceId: string }
  | { type: "release" }
  | { type: "release_all" }
  | { type: "input";  seq: number; ts: number; event: InputEvent }
  | { type: "tap_normalized"; seq: number; ts: number; x: number; y: number }
  | { type: "calibrate_pointer" }
  | { type: "video_subscribe"; deviceId: string }
  | { type: "webrtc_offer"; deviceId: string; sessionId: string; sdp: string }
  | { type: "webrtc_answer"; deviceId: string; sessionId: string; sdp: string }
  | { type: "ice_candidate"; deviceId: string; sessionId: string; candidate: string; sdpMid?: string | null; sdpMLineIndex?: number | null }
  | { type: "ping";   ts?: number };
```

**`input` / `tap_normalized` rules:**

- `seq` — integer, **strictly increasing** within a control session. Used for
  browser→server dedup/staleness only. The hub assigns a separate monotonic
  outbound seq to the ESP for each HID event (including synthetic calibrate/tap
  batches).
- `ts` — client epoch milliseconds. Dropped when `now - ts > STALE_COMMAND_MS`.
- `tap_normalized` requires pointer calibration state `READY` and an active
  control claim. Coords are `0..1` in the phone's current orientation.

**`calibrate_pointer`:** homes the HID cursor with many `move(-40,-40)`, then
marks calibration `READY` at estimated `(0,0)`.

**WebRTC:** only relayed for the **same `deviceId`** between an owner browser
that has claimed or `video_subscribe`d and that device's video agent. Wrong
device or stale `sessionId` ⇒ `{ error, reason: "stale_session" | "not_subscribed" | … }`.

A successful `claim` also auto-subscribes video for that device.

---

## 4. Server → Browser

```ts
interface DeviceSummary {
  id: string;
  name: string;
  online: boolean;              // back-compat alias of controllerOnline
  deviceOnline: boolean;        // alias of controllerOnline
  controllerOnline: boolean;    // ESP connected
  videoAgentOnline: boolean;
  videoStreaming: boolean;
  hidReady: boolean;            // MVP: == controllerOnline
  webRtcConnected: boolean;
  lastSeen: number | null;
  controlledByYou: boolean;
  busy: boolean;
}

type ServerToBrowser =
  | { type: "welcome"; sessionId: string; userId: string }
  | { type: "devices"; devices: DeviceSummary[] }
  | { type: "claimed"; deviceId: string; controlSessionId: string }
  | { type: "claim_failed"; deviceId: string; reason: string }
  | { type: "released"; deviceId: string }
  | { type: "device_status"; deviceId: string; online: boolean }
  | { type: "video_status"; deviceId: string; videoAgentOnline: boolean; videoStreaming: boolean; webRtcConnected: boolean; sessionId?: string }
  | { type: "webrtc_offer" | "webrtc_answer"; deviceId: string; sessionId: string; sdp: string }
  | { type: "ice_candidate"; deviceId: string; sessionId: string; candidate: string; … }
  | { type: "video_metadata"; deviceId: string; width: number; height: number; orientation?: string; fps?: number }
  | { type: "source_lost"; deviceId: string; reason?: string }
  | { type: "stream_state"; deviceId: string; state: string; detail?: string }
  | { type: "calibration_state"; state: string }  // UNCALIBRATED|CALIBRATING|READY|INVALID
  | { type: "error"; reason: string }
  | { type: "pong"; ts?: number };
```

---

## 5. Server → Device

```ts
type ServerToDevice =
  | { type: "hello"; deviceId: string }
  | { type: "input"; session: string; seq: number; event: InputEvent }
  | { type: "release_all" }
  | { type: "ping"; ts?: number };
```

Unchanged from the control-only MVP. Firmware is not modified for video.

---

## 6. Device → Server

```ts
type DeviceToServer =
  | { type: "status"; note?: string }
  | { type: "pong"; ts?: number };
```

---

## 7. Agent ↔ Server (VIDEO)

Canonical types are **snake_case**. The server also accepts PascalCase aliases
from the Windows agent (`WebrtcOffer` → `webrtc_offer`, etc.).

```ts
type AgentToServer =
  | { type: "register"; agentId?: string; capabilities?: string[] }
  | { type: "heartbeat"; ts?: number }
  | { type: "webrtc_offer" | "webrtc_answer"; sessionId: string; sdp: string; deviceId?: string }
  | { type: "ice_candidate"; sessionId: string; candidate: string; … }
  | { type: "video_metadata"; width: number; height: number; orientation?: string; fps?: number }
  | { type: "source_lost"; reason?: string }
  | { type: "stream_state"; state: string; detail?: string }
  | { type: "error"; code?: string; message?: string };

type ServerToAgent =
  | { type: "registered"; deviceId: string; agentId: string }
  | { type: "heartbeat_ack"; ts?: number }
  | { type: "stream_start"; sessionId: string; deviceId: string }
  | { type: "stream_stop"; sessionId?: string; deviceId?: string }
  | { type: "webrtc_offer" | "webrtc_answer"; deviceId: string; sessionId: string; sdp: string }
  | { type: "ice_candidate"; deviceId: string; sessionId: string; candidate: string; … }
  | { type: "error"; reason: string };
```

On `video_subscribe` (or claim auto-subscribe) the hub mints a `sessionId`,
sends `stream_start` to the agent, and returns it on `video_status` to the
browser. WebRTC messages with a mismatched session are rejected as
`stale_session`. Signaling is never cross-routed to another device's agent.

Orientation changes in `video_metadata` invalidate pointer calibration
(`INVALID`) for browsers controlling that device.

---

## 8. Heartbeat & liveness

Liveness uses **WebSocket-level ping/pong** frames for browser, device, and
agent sockets:

- Server pings every `HEARTBEAT_INTERVAL_MS` (default 10 s).
- If a socket does not pong within `HEARTBEAT_TIMEOUT_MS` (default 30 s) it is
  terminated, which runs the normal disconnect handling.

---

## 9. Audit log (info, no secrets)

The hub emits structured info logs (no secrets, no text contents) for:

- control session begin / end
- video session start / end
- controller connect / disconnect
- video agent connect / disconnect

---

## 10. Example session (control + video)

```
# ESP
WS  /ws/device  (x-device-id, x-device-secret)
      ← { "type":"hello", "deviceId":"esp32-lab-01" }

# Windows agent
WS  /ws/agent   (x-device-id, x-agent-id, x-agent-secret)
      ← { "type":"registered", "deviceId":"esp32-lab-01", "agentId":"windows-agent-01" }

# browser
WS  /ws/browser?token=...
   → { "type":"claim", "deviceId":"esp32-lab-01" }
      ← { "type":"claimed", … }
      ← { "type":"video_status", …, "sessionId":"…" }
        agent ← { "type":"stream_start", "sessionId":"…", "deviceId":"…" }
   → { "type":"calibrate_pointer" }
        device ← many { "type":"input", event:{kind:"move",dx:-40,dy:-40} }
      ← { "type":"calibration_state", "state":"READY" }
   → { "type":"webrtc_offer", deviceId, sessionId, sdp }
        agent ← { "type":"webrtc_offer", … }
```
