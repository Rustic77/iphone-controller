# Soak test metrics

`RemotePhone.Agent.Core.Reliability.SoakMetrics` is the in-process snapshot used for long-running reliability runs (Phase 5). Populate it from capture + WebRTC + signaling watchdogs; export periodically (log, file, or diagnostics UI).

## Fields

| Property | Meaning |
|----------|---------|
| `Runtime` | Wall time since soak start |
| `CaptureFps` | Producer / Graphics Capture FPS |
| `StreamFps` | WebRTC outbound (or browser inbound) FPS |
| `DroppedFrames` | Frames discarded by bounded queue or encoder path |
| `ReconnectCount` | WebRTC or signaling reconnect attempts |
| `SourceReconnectCount` | AirPlay / HWND source lost → restored cycles |
| `ErrorCount` | Hard faults (capture Error state, PC Failed, unhandled pipeline errors) |
| `MemoryBytes` | Working set or managed heap sample (document which in the run log) |

## How to run a soak

1. Establish a stable Phase 1 preview (and Phase 2 stream if testing media).
2. Start soak mode / timer; reset counters.
3. Leave mirroring + agent + (optional) browser receiver running for the planned window (e.g. 2–8 hours).
4. Sample metrics every N seconds; retain the series.
5. Inject planned failures from [FAILURE_MODE_MATRIX.md](FAILURE_MODE_MATRIX.md) (close receiver, kill WS, etc.) and confirm recovery counters increment without unbounded memory growth.
6. End soak: archive final `SoakMetrics` + logs (no frame contents).

## Pass / fail guidance

- **Pass:** Memory roughly flat or bounded; `DroppedFrames` explained by load; reconnects recover; `ErrorCount` within agreed budget; stream/preview usable at end.
- **Fail:** Monotonic memory growth, reconnect storms without backoff, permanent black frames, or agent crash requiring OS reboot.

`ExponentialBackoff` must cap reconnect delay (`maxDelayMs`) so soak reconnect pressure cannot spin the CPU.

## Relation to other modules

- `BoundedFrameQueue` → feeds `DroppedFrames`
- Capture state machine `SourceLost` → `SourceReconnectCount`
- `IWebRtcStreamingService.ReconnectAsync` → `ReconnectCount`
- Heartbeats keep signaling liveness visible even when `StreamFps` is zero (stall case in the failure matrix)
