/**
 * Shared wire types for the control + video relay.
 *
 * Three independent WebSocket populations connect to the server:
 *   - Devices  (ESP32-S3 firmware) on  /ws/device   → CONTROL path
 *   - Browsers (operator UI)        on  /ws/browser
 *   - Agents   (Windows video)      on  /ws/agent    → VIDEO path
 *
 * CONTROL and VIDEO are independent: an ESP offline does not imply the video
 * agent is offline, and vice versa. The server never trusts a socket to name
 * itself; identity comes from authentication, not from message fields.
 */

export type MouseButton = "left" | "right" | "middle";

/** A single HID-level input event produced by the operator UI. */
export type InputEvent =
  | { kind: "move"; dx: number; dy: number }
  | { kind: "click"; button: MouseButton; pressed: boolean }
  | { kind: "scroll"; dx: number; dy: number }
  | { kind: "text"; text: string }
  | { kind: "key"; code: string; pressed: boolean }
  | { kind: "release_all" };

/* -------------------------------------------------------------------------- */
/* Browser -> Server                                                          */
/* -------------------------------------------------------------------------- */

export type BrowserToServer =
  | { type: "claim"; deviceId: string }
  | { type: "release" }
  | { type: "release_all" }
  /** A control message. seq is per control-session monotonic; ts is client epoch ms. */
  | { type: "input"; seq: number; ts: number; event: InputEvent }
  /** Normalized video tap (0..1). Requires calibrated pointer + active control claim. */
  | { type: "tap_normalized"; seq: number; ts: number; x: number; y: number }
  /** Home the HID pointer to (0,0) via relative moves, then mark READY. */
  | { type: "calibrate_pointer" }
  | { type: "video_subscribe"; deviceId: string }
  | {
      type: "webrtc_offer";
      deviceId: string;
      sessionId: string;
      sdp: string;
    }
  | {
      type: "webrtc_answer";
      deviceId: string;
      sessionId: string;
      sdp: string;
    }
  | {
      type: "ice_candidate";
      deviceId: string;
      sessionId: string;
      candidate: string;
      sdpMid?: string | null;
      sdpMLineIndex?: number | null;
    }
  | { type: "ping"; ts?: number };

/* -------------------------------------------------------------------------- */
/* Server -> Browser                                                          */
/* -------------------------------------------------------------------------- */

export interface DeviceSummary {
  id: string;
  name: string;
  /**
   * Back-compat alias of `controllerOnline` (ESP connected).
   * Existing clients treat this as "device is reachable for control".
   */
  online: boolean;
  /** Alias of ESP controller online (same as `controllerOnline` / `online`). */
  deviceOnline: boolean;
  /** ESP controller connected. */
  controllerOnline: boolean;
  /** Windows video agent connected for this device. */
  videoAgentOnline: boolean;
  /** Agent reports an active capture/stream. */
  videoStreaming: boolean;
  /** MVP: true whenever the ESP controller is online. */
  hidReady: boolean;
  /** WebRTC peer connection established between browser and agent. */
  webRtcConnected: boolean;
  /** epoch ms of last activity, or null if never seen this process. */
  lastSeen: number | null;
  /** true if THIS browser session currently controls the device. */
  controlledByYou: boolean;
  /** true if some other operator/session currently controls the device. */
  busy: boolean;
}

export type ServerToBrowser =
  | { type: "welcome"; sessionId: string; userId: string }
  | { type: "devices"; devices: DeviceSummary[] }
  | { type: "claimed"; deviceId: string; controlSessionId: string }
  | { type: "claim_failed"; deviceId: string; reason: string }
  | { type: "released"; deviceId: string }
  | { type: "device_status"; deviceId: string; online: boolean }
  | {
      type: "video_status";
      deviceId: string;
      videoAgentOnline: boolean;
      videoStreaming: boolean;
      webRtcConnected: boolean;
      /** Present after a successful video_subscribe / claim auto-subscribe. */
      sessionId?: string;
    }
  | {
      type: "webrtc_offer";
      deviceId: string;
      sessionId: string;
      sdp: string;
    }
  | {
      type: "webrtc_answer";
      deviceId: string;
      sessionId: string;
      sdp: string;
    }
  | {
      type: "ice_candidate";
      deviceId: string;
      sessionId: string;
      candidate: string;
      sdpMid?: string | null;
      sdpMLineIndex?: number | null;
    }
  | {
      type: "video_metadata";
      deviceId: string;
      width: number;
      height: number;
      orientation?: string;
      fps?: number;
    }
  | { type: "source_lost"; deviceId: string; reason?: string }
  | { type: "stream_state"; deviceId: string; state: string; detail?: string }
  | { type: "calibration_state"; state: string }
  | { type: "error"; reason: string }
  | { type: "pong"; ts?: number };

/* -------------------------------------------------------------------------- */
/* Server -> Device                                                           */
/* -------------------------------------------------------------------------- */

export type ServerToDevice =
  | { type: "hello"; deviceId: string }
  /**
   * A relayed control message. `session` is the control-session id the device
   * uses to (a) reset its sequence space when a new operator takes over and
   * (b) reject messages from any other session. `seq` is monotonic within a
   * session. A `release_all` always precedes any change of `session`.
   */
  | { type: "input"; session: string; seq: number; event: InputEvent }
  | { type: "release_all" }
  | { type: "ping"; ts?: number };

/* -------------------------------------------------------------------------- */
/* Device -> Server                                                           */
/* -------------------------------------------------------------------------- */

export type DeviceToServer =
  | { type: "status"; note?: string }
  | { type: "pong"; ts?: number };

/* -------------------------------------------------------------------------- */
/* Agent (Windows video) -> Server                                            */
/* -------------------------------------------------------------------------- */

export type AgentToServer =
  | { type: "register"; agentId?: string; capabilities?: string[] }
  | { type: "heartbeat"; ts?: number }
  | {
      type: "webrtc_offer";
      sessionId: string;
      sdp: string;
      deviceId?: string;
    }
  | {
      type: "webrtc_answer";
      sessionId: string;
      sdp: string;
      deviceId?: string;
    }
  | {
      type: "ice_candidate";
      sessionId: string;
      candidate: string;
      sdpMid?: string | null;
      sdpMLineIndex?: number | null;
      deviceId?: string;
    }
  | {
      type: "video_metadata";
      width: number;
      height: number;
      orientation?: string;
      fps?: number;
    }
  | { type: "source_lost"; reason?: string }
  | { type: "stream_state"; state: string; detail?: string }
  | { type: "error"; code?: string; message?: string };

/* -------------------------------------------------------------------------- */
/* Server -> Agent                                                            */
/* -------------------------------------------------------------------------- */

export type ServerToAgent =
  | { type: "registered"; deviceId: string; agentId: string }
  | { type: "heartbeat_ack"; ts?: number }
  | {
      type: "stream_start";
      sessionId: string;
      deviceId: string;
    }
  | { type: "stream_stop"; sessionId?: string; deviceId?: string }
  | {
      type: "webrtc_offer";
      deviceId: string;
      sessionId: string;
      sdp: string;
    }
  | {
      type: "webrtc_answer";
      deviceId: string;
      sessionId: string;
      sdp: string;
    }
  | {
      type: "ice_candidate";
      deviceId: string;
      sessionId: string;
      candidate: string;
      sdpMid?: string | null;
      sdpMLineIndex?: number | null;
    }
  | { type: "error"; reason: string };

/** Minimal transport abstraction so the Hub can be tested without real sockets. */
export interface Transport {
  send(data: string): void;
  close(code?: number, reason?: string): void;
}
