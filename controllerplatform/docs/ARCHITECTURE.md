# Architecture

## Goal

Let an authenticated operator control a USB-HID iPhone rig (driven by an
ESP32-S3) from a browser, over the Internet, **without exposing the ESP32** to
inbound connections or requiring router port forwarding.

## Topology

```
   Browser (operator UI)
      │  HTTPS + WSS  (session token)
      ▼
┌─────────────────────────────┐
│   Cloud control server      │   ← this repo
│                             │
│   • Fastify (HTTP + static) │
│   • ws (WebSocket relay)    │
│   • Hub (routing + policy)  │
└─────────────────────────────┘
      ▲
      │  outbound WSS/TLS  (device secret)
      │
   ESP32-S3 firmware
      │  USB HID (mouse + keyboard)
      ▼
   iPhone
```

Key property: **the ESP32 initiates the connection outward** to the server. The
server is the only publicly reachable component. This removes the need to expose
the device or forward ports, and it means firewalls/NAT on the device side are
never in the way.

## Components

### Fastify HTTP layer (`src/server.ts`)
- Serves the static web UI from `public/`.
- `POST /api/login` — operator login, returns a signed session token.
- `GET /api/health` — liveness probe.
- Provides the underlying Node HTTP server that the WebSocket layer attaches to.
- Emits structured JSON logs (pino).

### WebSocket layer (`src/server.ts`)
Two logical endpoints, authenticated **at the HTTP upgrade** before any relay
logic runs:

- `/ws/browser?token=…` — operator sockets. Token verified by `SessionManager`.
- `/ws/device` — device sockets. `x-device-id` + `x-device-secret` headers
  verified by the device store. (Query params are accepted as a fallback for
  browser-based testing.)

Both are `ws` servers in `noServer` mode; a single `upgrade` handler routes by
path, authenticates, and only then calls `handleUpgrade`. Failed auth ⇒ `401`
and the socket is destroyed.

Each accepted browser socket gets a fresh **connection id** (`clientId`),
independent of the auth session, so two browser tabs sharing one token are two
independent control clients.

### The Hub (`src/hub.ts`)
The relay core and policy engine. It is **transport-agnostic**: it talks to
sockets only through a tiny `Transport` interface (`send` / `close`), which is
what makes it fully unit-testable without real networking. Responsibilities:

- Track connected devices (online/offline, last-seen, current controller).
- Track connected browser clients (which device each controls).
- Enforce the security invariants (below).
- Route validated input from a browser to exactly the device it controls.
- Fan out device-list updates to the owning operator's browser clients.

### Sequence tracker (`src/sequenceTracker.ts`)
Per-control-session gate that drops duplicate/out-of-order (`seq <= last`) and
stale (`now - ts > staleMs`) commands. A new tracker is created on every claim,
so each control session has its own sequence space.

### Credential model
See [Credential model](#credential-model).

## Security invariants

1. **No unauthenticated control.** Every WebSocket upgrade is authenticated
   before the socket is accepted.
2. **Tenant isolation.** Each device has an `ownerId`. A browser client may only
   list, claim, or control devices whose `ownerId` equals its authenticated
   `userId`. Claiming a device you don't own returns `not_found` (existence is
   not revealed).
3. **Routed, not broadcast.** Input is delivered only to the single device the
   sending client currently controls, and only while `device.controllingSessionId`
   still equals that client. One user's input can never reach another user's
   device.
4. **Single controller.** A device is controlled by at most one client at a time;
   a second claim gets `busy`.
5. **Fail safe on disconnect.** If the controlling browser drops, the device is
   told to `release_all` (drop all HID buttons/keys). If the device drops, the
   controlling browser is told immediately and its claim is cleared.

## Lifecycle flows

### Device comes online
```
ESP32 ──WSS──▶ /ws/device (id+secret)
server: verifyDevice → Hub.addDevice → mark online, send {hello}
server → owner's browsers: {devices} snapshot (device now online)
```

### Operator claims + controls
```
browser → {claim, deviceId}
Hub: owner? online? free? → set controller, new SequenceTracker
     → {claimed, controlSessionId}
browser → {input, seq, ts, event}  (repeatedly)
Hub: owns+controls? seq ok? not stale? → device {input, seq, event}
```

### Browser disconnects (fail-safe)
```
socket close → Hub.removeBrowser
Hub: was controlling? → device {release_all}; clear claim
```

### Device disconnects
```
socket close → Hub.removeDevice(id, transport)   // transport guard vs. reconnects
Hub: mark offline; controlling browser → {device_status offline} + {released}
     → owner's browsers: {devices} snapshot (device now offline)
```

### Heartbeat / timeout
The server pings every `HEARTBEAT_INTERVAL_MS`; if a socket hasn't ponged within
`HEARTBEAT_TIMEOUT_MS` it is `terminate()`d, which triggers the normal close
handling above. This catches half-open TCP connections (e.g. device loses power)
that never send a FIN.

## Credential model

Intentionally minimal and swappable behind two interfaces so it can be replaced
by full account authentication later without touching the relay core:

| Concern            | Interface                       | MVP implementation                     | Replace with                          |
| ------------------ | ------------------------------- | -------------------------------------- | ------------------------------------- |
| Operator identity  | `UserStore`                     | `DevUserStore` (env `DEV_USERNAME/PW`) | DB users + hashed passwords / OAuth   |
| Browser sessions   | `SessionManager`                | HMAC-signed token + in-memory table    | JWT / server-side session store       |
| Device identity    | `DeviceCredentialStore`         | `InMemoryDeviceStore` (JSON seed file) | DB-backed provisioning + rotation     |

Session tokens are `base64url(payload).base64url(HMAC-SHA256(payload))` where
`payload = { sid, uid, exp }`, signed with `SERVER_SECRET`. The server also keeps
an in-memory session table so sessions can be revoked and aren't trusted on
signature alone.

Device credentials are long-lived per-device secrets compared in constant time.
"Registration" in the MVP = presence in `devices.json`.

## Deliberate non-goals (MVP)

- **Video streaming** — out of scope for now.
- **Horizontal scale** — state is in-process. A device and its controlling
  browser must land on the same server instance. For multi-instance, add a
  shared bus (e.g. Redis pub/sub) keyed by device id and a shared session store.
- **Persistent history** — device/session state is in-memory only.

## Scaling note

Because routing is by in-memory maps, the MVP is single-instance. The Hub's
transport abstraction makes the natural next step clear: replace direct
`transport.send` to a remote device with a publish to a message bus that the
owning instance consumes. No policy logic needs to change.
