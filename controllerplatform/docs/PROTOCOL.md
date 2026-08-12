# Wire Protocol

Transport: **WebSocket**. Every frame is a single JSON object with a `type`
field (a discriminated union). Definitions live in
[`src/types.ts`](../src/types.ts) — that file is the source of truth.

There are two independent socket populations:

- **Browser** operator sockets on `/ws/browser`
- **Device** (ESP32) sockets on `/ws/device`

The server never trusts a socket to name itself; identity is established at
authentication (below), not from message fields.

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

### Device socket

```
GET /ws/device
x-device-id:     <deviceId>
x-device-secret: <secret>
```

(For browser-based testing, `?deviceId=…&secret=…` query params are also
accepted.) Unknown id or wrong secret ⇒ `401`.

---

## 2. Input events

The atomic unit of control. Produced by the operator UI, relayed verbatim to the
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
  | { type: "claim";  deviceId: string }                    // request control
  | { type: "release" }                                     // give up control
  | { type: "release_all" }                                 // emergency stop (keeps claim)
  | { type: "input";  seq: number; ts: number; event: InputEvent }
  | { type: "ping";   ts?: number };
```

**`input` rules:**

- `seq` — integer, **strictly increasing** within a control session (i.e. since
  the last successful `claim`). The server drops `seq <= lastAccepted`
  (duplicate / out-of-order).
- `ts` — client epoch milliseconds. The server drops commands where
  `now - ts > STALE_COMMAND_MS` (default 2000 ms).
- Dropped commands are silently discarded (logged at `debug`); they are not
  acknowledged or errored.
- Sending `input` without controlling the target device ⇒ `{ error, reason: "not_controlling" }`.

---

## 4. Server → Browser

```ts
type ServerToBrowser =
  | { type: "welcome"; sessionId: string; userId: string }
  | { type: "devices"; devices: DeviceSummary[] }          // full snapshot, pushed on change
  | { type: "claimed"; deviceId: string; controlSessionId: string }
  | { type: "claim_failed"; deviceId: string; reason: string } // "not_found" | "offline" | "busy"
  | { type: "released"; deviceId: string }
  | { type: "device_status"; deviceId: string; online: boolean }
  | { type: "error"; reason: string }
  | { type: "pong"; ts?: number };

interface DeviceSummary {
  id: string;
  name: string;
  online: boolean;
  lastSeen: number | null;   // epoch ms
  controlledByYou: boolean;
  busy: boolean;             // controlled by another session
}
```

- On connect the browser receives `welcome` then a `devices` snapshot.
- `devices` is re-pushed whenever the owner's fleet changes (device on/offline,
  claim/release). The UI can treat it as authoritative and re-render.
- `claim_failed.reason`:
  - `not_found` — device doesn't exist **or the operator doesn't own it**
    (existence intentionally not distinguished).
  - `offline` — device is not currently connected.
  - `busy` — another session already controls it.

---

## 5. Server → Device

```ts
type ServerToDevice =
  | { type: "hello"; deviceId: string }         // sent right after auth
  | { type: "input"; session: string; seq: number; event: InputEvent } // relayed, post-validation
  | { type: "release_all" }                     // drop all HID state NOW
  | { type: "ping"; ts?: number };
```

The device receives `input` **only** after the server has validated ownership,
control, sequence, and staleness — the firmware can apply events directly.

`session` is the control-session id (a new one is minted on every successful
`claim`). It lets the device enforce its own session/sequence discipline:

- `seq` is strictly increasing **within a `session`**; the device drops
  `seq <= last` (duplicate / out-of-order).
- The device latches the first `session` it sees and **rejects** input from any
  other session until reset.
- The server guarantees a `release_all` is sent to the device **before** any
  change of `session` (on release, device switch, browser disconnect, or a
  re-claim), so the device resets its sequence space and adopts the new session
  cleanly. This is also why a cloud reconnect never replays old input: a fresh
  connection starts with no latched session and `seq` begins again.

The firmware **must** handle `release_all` by releasing every held mouse button
and keyboard key. The server sends it on:

- emergency stop (operator pressed RELEASE ALL),
- explicit `release`,
- controlling browser disconnect.

---

## 6. Device → Server

```ts
type DeviceToServer =
  | { type: "status"; note?: string }   // optional device-reported status
  | { type: "pong"; ts?: number };
```

Any message from the device refreshes its `lastSeen`. Devices are sinks; they do
not route anything to browsers in the MVP beyond liveness.

---

## 7. Heartbeat & liveness

Liveness uses **WebSocket-level ping/pong** frames (not the app-level `ping`
messages above, which are a convenience echo):

- Server pings every `HEARTBEAT_INTERVAL_MS` (default 10 s).
- If a socket does not pong within `HEARTBEAT_TIMEOUT_MS` (default 30 s) it is
  terminated, which runs the normal disconnect handling (device → offline +
  notify; browser → `release_all` to its device).

Clients/firmware only need to answer WS pings (the `ws` library and browsers do
this automatically) to stay alive.

---

## 8. Error handling

- Malformed JSON from a browser ⇒ `{ error, reason: "bad_json" }`.
- Unknown `type` from a browser ⇒ `{ error, reason: "unknown_message_type" }`.
- Malformed JSON from a device is ignored.
- The server never throws on bad input; unparseable/invalid messages are dropped.

---

## 9. Example session

```
# operator
POST /api/login {admin, ****}            → { token }

# device (ESP32)
WS  /ws/device  (x-device-id, x-device-secret)
      ← { "type":"hello", "deviceId":"esp32-lab-01" }

# browser
WS  /ws/browser?token=...
      ← { "type":"welcome", "sessionId":"…", "userId":"dev-operator" }
      ← { "type":"devices", "devices":[{ "id":"esp32-lab-01","online":true,… }] }
   → { "type":"claim", "deviceId":"esp32-lab-01" }
      ← { "type":"claimed", "deviceId":"esp32-lab-01", "controlSessionId":"…" }
   → { "type":"input", "seq":1, "ts":1786490000000, "event":{"kind":"move","dx":12,"dy":-3} }
        device ← { "type":"input", "seq":1, "event":{"kind":"move","dx":12,"dy":-3} }
   → { "type":"input", "seq":1, … }        # duplicate seq → dropped, not forwarded
   → { "type":"release_all" }              # emergency
        device ← { "type":"release_all" }
   (browser socket closes)
        device ← { "type":"release_all" }  # fail-safe on disconnect
```
