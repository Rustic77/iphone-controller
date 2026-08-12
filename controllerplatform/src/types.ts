/**
 * Shared wire types for the control relay.
 *
 * Two independent WebSocket populations connect to the server:
 *   - Devices  (ESP32-S3 firmware) on  /ws/device
 *   - Browsers (operator UI)        on  /ws/browser
 *
 * The server never trusts a socket to name itself; identity comes from
 * authentication (see src/auth.ts), not from message fields.
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
  | { type: "ping"; ts?: number };

/* -------------------------------------------------------------------------- */
/* Server -> Browser                                                          */
/* -------------------------------------------------------------------------- */

export interface DeviceSummary {
  id: string;
  name: string;
  online: boolean;
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

/** Minimal transport abstraction so the Hub can be tested without real sockets. */
export interface Transport {
  send(data: string): void;
  close(code?: number, reason?: string): void;
}
