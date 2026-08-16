# Signaling protocol (Agent WebRTC)

JSON messages over a secure WebSocket between the Windows Agent and the signaling server (and, for lab tests, a browser page). Types and payloads match `RemotePhone.Agent.Core.Signaling` (`SignalingMessages.cs`).

Property names serialize as **camelCase**. Enums serialize as camelCase strings (e.g. `portrait`, `capturing`).

## Common envelope fields

All messages share:

| Field | Type | Notes |
|-------|------|-------|
| `type` | string | Discriminator (required) |
| `deviceId` | string? | Logical device association |
| `agentId` | string? | Agent instance id |
| `sessionId` | string? | Active stream session; gated by `SessionGate` |

## Message catalog

### `AgentRegister`

Agent → server. Authenticate before streaming.

| Field | Type |
|-------|------|
| `credential` | string |

### `AgentAuthenticated`

Server → agent.

| Field | Type |
|-------|------|
| `success` | bool |
| `message` | string? |

Streaming / capture must not start until `success` is true.

### `StreamStart`

Server → agent. Begin (or restart) a media session.

| Field | Type |
|-------|------|
| `preferredResolution` | string? |
| `preferredFps` | int? |
| `preferredBitrate` | int? |

On accept: `SessionGate.SetSession(sessionId)` for the new session.

### `StreamStop`

Server → agent. End session. Agent should `SessionGate.Clear()` and tear down WebRTC/capture as designed.

### `WebrtcOffer` / `WebrtcAnswer`

SDP exchange.

| Field | Type |
|-------|------|
| `sdp` | string |

### `IceCandidate`

| Field | Type |
|-------|------|
| `candidate` | string |
| `sdpMid` | string? |
| `sdpMLineIndex` | int? |

### `VideoMetadata`

Agent → clients. Frame geometry / orientation / fps.

| Field | Type |
|-------|------|
| `width` | int |
| `height` | int |
| `orientation` | `portrait` \| `landscape` |
| `fps` | number |

### `Heartbeat` / `HeartbeatAck`

Liveness. Both carry:

| Field | Type |
|-------|------|
| `timestampUtc` | ISO-8601 DateTimeOffset |

### `StreamState`

| Field | Type |
|-------|------|
| `state` | `idle` \| `selecting` \| `capturing` \| `sourceLost` \| `stopped` \| `error` |
| `detail` | string? |

### `SourceLost`

| Field | Type |
|-------|------|
| `reason` | string? |

### `Error`

| Field | Type |
|-------|------|
| `code` | string (default `error`) |
| `message` | string |

---

The control relay (`controllerplatform`) sends **snake_case** types (`stream_start`,
`webrtc_answer`, `ice_candidate`, `registered`). The agent accepts those aliases
as well as the PascalCase names in this catalog.

## Session behavior

1. **Connect** to `/ws/agent` (header auth). Hub replies `registered`.
2. Wait for **StreamStart** (`stream_start`) — this is the hub's `sessionId`.
   Do not create a WebRTC offer until this arrives; a locally generated session
   id is rejected as `stale_session`.
3. Agent is the offerer: **WebrtcOffer** with the hub session id. Browser answers.
3. Negotiate **WebrtcOffer** / **WebrtcAnswer** / **IceCandidate** for that session only.
4. Publish **VideoMetadata** and **StreamState** as capture evolves.
5. **Heartbeat** ↔ **HeartbeatAck** while connected.
6. **StreamStop** or failure → clear session; ignore further media signaling until a new start.

## Stale rejection (`SessionGate`)

`SessionGate.Accept(sessionId)` returns:

- `false` if no current session is set
- `false` if `sessionId` is null or not equal (ordinal) to the current id
- `true` only for the active session

**Rule:** Ignore `WebrtcOffer`, `WebrtcAnswer`, and `IceCandidate` (and any other session-scoped media control) when `Accept` is false. This prevents cross-talk after `StreamStop` or a new `StreamStart` with a different id.

---

## Lab browser endpoint

The smoke page [`test-receiver.html`](test-receiver.html) connects to:

```text
ws://localhost:8080/ws/browser-video
```

Replace host/port/path to match your Phase 2 signaling server. See README for the Phase 1 vs Phase 2 boundary.
