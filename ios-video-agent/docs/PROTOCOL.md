# iOS video agent protocol

The iOS broadcast extension is a **video agent** on the same wire as the Windows agent.

- Socket: `GET /ws/agent`
- Headers: `x-device-id`, `x-agent-id` (default `ios-agent-01`), `x-agent-secret` (same per-device secret as the ESP)
- JSON types: `register`, `webrtc_offer`, `ice_candidate`, `video_metadata`, `stream_state`, `source_lost`, `heartbeat`
- Server: `registered`, `stream_start`, `stream_stop`, `webrtc_answer`, `ice_candidate`

Canonical spec: [`controllerplatform/docs/PROTOCOL.md`](../controllerplatform/docs/PROTOCOL.md).
