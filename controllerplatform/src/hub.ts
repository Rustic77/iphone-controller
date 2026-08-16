import { randomBytes, randomUUID } from "node:crypto";
import type { Logger } from "./logger.js";
import { PointerCalibration } from "./pointerCalibration.js";
import type { DeviceCredentialStore, DeviceRecord } from "./stores/deviceStore.js";
import { SequenceTracker } from "./sequenceTracker.js";
import type {
  AgentToServer,
  BrowserToServer,
  DeviceSummary,
  DeviceToServer,
  InputEvent,
  ServerToAgent,
  ServerToBrowser,
  ServerToDevice,
  Transport,
} from "./types.js";

interface DeviceConn {
  record: DeviceRecord;
  transport: Transport;
  online: boolean;
  lastSeen: number | null;
  /** browser session id currently controlling this device, or null. */
  controllingSessionId: string | null;
}

interface VideoAgentConn {
  deviceId: string;
  agentId: string;
  transport: Transport;
  streaming: boolean;
  webRtcConnected: boolean;
  lastSeen: number;
  /** Active video session id (minted on subscribe / stream start). */
  videoSessionId: string | null;
}

interface BrowserConn {
  sessionId: string;
  userId: string;
  transport: Transport;
  /** device this session currently controls, or null. */
  controllingDeviceId: string | null;
  /** id of the current control session (regenerated on each claim). */
  controlSessionId: string | null;
  seq: SequenceTracker | null;
  /**
   * Monotonic outbound HID seq for the device within this control session.
   * Browser `seq` is only used for browser→server dedup/staleness; synthetic
   * calibrate/tap batches also consume this counter so the device never sees
   * colliding sequence numbers.
   */
  deviceOutSeq: number;
  /** device this session is subscribed to for video (may differ from control). */
  videoDeviceId: string | null;
  videoSessionId: string | null;
  calibration: PointerCalibration;
}

export interface HubDeps {
  deviceStore: DeviceCredentialStore;
  logger: Logger;
  staleCommandMs: number;
  now?: () => number;
}

/**
 * The relay core. Transport-agnostic so it can be unit-tested without real
 * sockets. It enforces the security invariants:
 *   - a browser may only claim/control/subscribe devices its user owns
 *   - HID input is routed ONLY to the device the session currently controls
 *   - WebRTC signaling is relayed ONLY for the same deviceId between an owner
 *     browser (claimed or video-subscribed) and that device's video agent
 *   - sequence + staleness gating drops duplicate/old commands
 *
 * CONTROL (ESP) and VIDEO (Windows agent) are independent paths.
 */
export class Hub {
  private readonly devices = new Map<string, DeviceConn>();
  private readonly videoAgents = new Map<string, VideoAgentConn>();
  private readonly browsers = new Map<string, BrowserConn>();
  /** userId -> set of that user's browser sessions (for fan-out). */
  private readonly browsersByUser = new Map<string, Set<BrowserConn>>();

  private readonly deviceStore: DeviceCredentialStore;
  private readonly log: Logger;
  private readonly staleMs: number;
  private readonly now: () => number;

  constructor(deps: HubDeps) {
    this.deviceStore = deps.deviceStore;
    this.log = deps.logger;
    this.staleMs = deps.staleCommandMs;
    this.now = deps.now ?? Date.now;
  }

  /* ---------------------------------------------------------------------- */
  /* Devices (ESP / CONTROL)                                                */
  /* ---------------------------------------------------------------------- */

  /** Register an authenticated device socket as online. */
  addDevice(record: DeviceRecord, transport: Transport): void {
    const existing = this.devices.get(record.id);
    if (existing && existing.transport !== transport) {
      this.log.warn({ deviceId: record.id }, "device reconnected; closing previous socket");
      existing.online = false;
      try {
        existing.transport.close(4000, "replaced by new connection");
      } catch {
        /* ignore */
      }
    }

    const conn: DeviceConn = {
      record,
      transport,
      online: true,
      lastSeen: this.now(),
      controllingSessionId: existing?.controllingSessionId ?? null,
    };
    this.devices.set(record.id, conn);
    this.send(transport, { type: "hello", deviceId: record.id });
    this.log.info({ deviceId: record.id, ownerId: record.ownerId }, "controller connect");
    this.notifyOwnerDevices(record.ownerId);
  }

  /**
   * Handle a device socket closing. The `transport` guard ensures a late close
   * from a replaced socket cannot knock the new connection offline.
   */
  removeDevice(deviceId: string, transport: Transport): void {
    const conn = this.devices.get(deviceId);
    if (!conn || conn.transport !== transport) return;

    conn.online = false;
    conn.lastSeen = this.now();
    this.devices.delete(deviceId);
    this.log.info({ deviceId }, "controller disconnect");

    const controllingSession = conn.controllingSessionId;
    if (controllingSession) {
      const browser = this.browsers.get(controllingSession);
      if (browser && browser.controllingDeviceId === deviceId) {
        browser.controllingDeviceId = null;
        browser.controlSessionId = null;
        browser.seq = null;
        browser.deviceOutSeq = 0;
        this.send(browser.transport, { type: "device_status", deviceId, online: false });
        this.send(browser.transport, { type: "released", deviceId });
        this.log.info(
          { sessionId: browser.sessionId, deviceId, reason: "controller disconnect" },
          "control session end",
        );
      }
    }
    this.notifyOwnerDevices(conn.record.ownerId);
  }

  handleDeviceMessage(deviceId: string, transport: Transport, msg: DeviceToServer): void {
    const conn = this.devices.get(deviceId);
    if (!conn || conn.transport !== transport) return;
    conn.lastSeen = this.now();
    // Devices are sinks; status/pong just refresh liveness. Nothing to route.
  }

  /* ---------------------------------------------------------------------- */
  /* Video agents (VIDEO path)                                              */
  /* ---------------------------------------------------------------------- */

  addVideoAgent(deviceId: string, agentId: string, transport: Transport): void {
    const record = this.deviceStore.getDevice(deviceId);
    if (!record) {
      this.sendAgent(transport, { type: "error", reason: "unknown_device" });
      try {
        transport.close(4001, "unknown device");
      } catch {
        /* ignore */
      }
      return;
    }

    const existing = this.videoAgents.get(deviceId);
    if (existing && existing.transport !== transport) {
      this.log.warn({ deviceId, agentId }, "video agent reconnected; closing previous socket");
      try {
        existing.transport.close(4000, "replaced by new connection");
      } catch {
        /* ignore */
      }
    }

    const conn: VideoAgentConn = {
      deviceId,
      agentId,
      transport,
      streaming: false,
      webRtcConnected: false,
      lastSeen: this.now(),
      videoSessionId: existing?.videoSessionId ?? null,
    };
    this.videoAgents.set(deviceId, conn);
    this.sendAgent(transport, { type: "registered", deviceId, agentId });
    this.log.info({ deviceId, agentId }, "video agent connect");
    this.resumeVideoIfSubscribed(deviceId, conn);
    this.notifyOwnerDevices(record.ownerId);
    this.broadcastVideoStatus(deviceId);
  }

  removeVideoAgent(deviceId: string, transport: Transport): void {
    const conn = this.videoAgents.get(deviceId);
    if (!conn || conn.transport !== transport) return;

    const hadSession = conn.videoSessionId;
    this.videoAgents.delete(deviceId);
    this.log.info({ deviceId, agentId: conn.agentId }, "video agent disconnect");
    if (hadSession) {
      this.log.info({ deviceId, sessionId: hadSession }, "video session end");
    }

    // Clear video subscriptions that pointed at this agent session.
    for (const browser of this.browsers.values()) {
      if (browser.videoDeviceId === deviceId) {
        browser.videoDeviceId = null;
        browser.videoSessionId = null;
        this.send(browser.transport, {
          type: "video_status",
          deviceId,
          videoAgentOnline: false,
          videoStreaming: false,
          webRtcConnected: false,
        });
      }
    }

    const record = this.deviceStore.getDevice(deviceId);
    if (record) this.notifyOwnerDevices(record.ownerId);
  }

  /** If a browser already subscribed while the agent was offline, start that session now. */
  private resumeVideoIfSubscribed(deviceId: string, agent: VideoAgentConn): void {
    for (const browser of this.browsers.values()) {
      if (browser.videoDeviceId !== deviceId || !browser.videoSessionId) continue;
      agent.videoSessionId = browser.videoSessionId;
      agent.streaming = false;
      agent.webRtcConnected = false;
      this.sendAgent(agent.transport, {
        type: "stream_start",
        sessionId: browser.videoSessionId,
        deviceId,
      });
      this.log.info(
        { deviceId, videoSessionId: browser.videoSessionId },
        "video session resume on agent connect",
      );
      return;
    }
  }

  handleAgentMessage(deviceId: string, transport: Transport, msg: AgentToServer): void {
    const agent = this.videoAgents.get(deviceId);
    if (!agent || agent.transport !== transport) return;
    agent.lastSeen = this.now();

    switch (msg.type) {
      case "register":
        this.sendAgent(transport, { type: "registered", deviceId, agentId: agent.agentId });
        break;
      case "heartbeat":
        this.sendAgent(transport, { type: "heartbeat_ack", ts: msg.ts ?? this.now() });
        break;
      case "webrtc_offer":
      case "webrtc_answer":
      case "ice_candidate":
        this.relayAgentWebrtcToBrowsers(agent, msg);
        break;
      case "video_metadata":
        this.handleVideoMetadata(agent, msg);
        break;
      case "source_lost":
        agent.streaming = false;
        agent.webRtcConnected = false;
        this.fanoutToVideoBrowsers(deviceId, {
          type: "source_lost",
          deviceId,
          reason: msg.reason,
        });
        this.broadcastVideoStatus(deviceId);
        this.notifyOwnerOf(deviceId);
        break;
      case "stream_state":
        this.handleStreamState(agent, msg.state, msg.detail);
        break;
      case "error":
        this.log.warn(
          { deviceId, agentId: agent.agentId, code: msg.code },
          "video agent error",
        );
        this.fanoutToVideoBrowsers(deviceId, {
          type: "error",
          reason: msg.code ?? msg.message ?? "agent_error",
        });
        break;
      default:
        this.sendAgent(transport, { type: "error", reason: "unknown_message_type" });
    }
  }

  private handleStreamState(
    agent: VideoAgentConn,
    state: string,
    detail?: string,
  ): void {
    const s = state.toLowerCase();
    agent.streaming =
      s === "streaming" ||
      s === "capturing" ||
      s === "active" ||
      s === "running";
    agent.webRtcConnected =
      s === "connected" ||
      s === "webrtcconnected" ||
      s === "webrtc_connected" ||
      (agent.webRtcConnected && agent.streaming);

    if (s === "disconnected" || s === "idle" || s === "stopped" || s === "failed") {
      agent.webRtcConnected = false;
      if (s === "idle" || s === "stopped") agent.streaming = false;
    }

    this.fanoutToVideoBrowsers(agent.deviceId, {
      type: "stream_state",
      deviceId: agent.deviceId,
      state,
      detail,
    });
    this.broadcastVideoStatus(agent.deviceId);
    this.notifyOwnerOf(agent.deviceId);
  }

  private handleVideoMetadata(
    agent: VideoAgentConn,
    msg: Extract<AgentToServer, { type: "video_metadata" }>,
  ): void {
    this.fanoutToVideoBrowsers(agent.deviceId, {
      type: "video_metadata",
      deviceId: agent.deviceId,
      width: msg.width,
      height: msg.height,
      orientation: msg.orientation,
      fps: msg.fps,
    });

    // Orientation changes invalidate pointer calibration for controllers of this device.
    if (msg.orientation) {
      for (const browser of this.browsers.values()) {
        if (browser.controllingDeviceId === agent.deviceId) {
          const before = browser.calibration.state;
          browser.calibration.setOrientation(msg.orientation);
          if (browser.calibration.state !== before) {
            this.send(browser.transport, {
              type: "calibration_state",
              state: browser.calibration.state,
            });
          }
        }
      }
    }
  }

  private relayAgentWebrtcToBrowsers(
    agent: VideoAgentConn,
    msg: Extract<
      AgentToServer,
      { type: "webrtc_offer" | "webrtc_answer" | "ice_candidate" }
    >,
  ): void {
    if (msg.deviceId && msg.deviceId !== agent.deviceId) {
      this.sendAgent(agent.transport, { type: "error", reason: "device_mismatch" });
      return;
    }
    if (!agent.videoSessionId || msg.sessionId !== agent.videoSessionId) {
      this.sendAgent(agent.transport, { type: "error", reason: "stale_session" });
      return;
    }

    const payload: ServerToBrowser =
      msg.type === "ice_candidate"
        ? {
            type: "ice_candidate",
            deviceId: agent.deviceId,
            sessionId: msg.sessionId,
            candidate: msg.candidate,
            sdpMid: msg.sdpMid,
            sdpMLineIndex: msg.sdpMLineIndex,
          }
        : {
            type: msg.type,
            deviceId: agent.deviceId,
            sessionId: msg.sessionId,
            sdp: msg.sdp,
          };

    this.fanoutToVideoBrowsers(agent.deviceId, payload, msg.sessionId);
  }

  /* ---------------------------------------------------------------------- */
  /* Browsers                                                               */
  /* ---------------------------------------------------------------------- */

  addBrowser(sessionId: string, userId: string, transport: Transport): void {
    const conn: BrowserConn = {
      sessionId,
      userId,
      transport,
      controllingDeviceId: null,
      controlSessionId: null,
      seq: null,
      deviceOutSeq: 0,
      videoDeviceId: null,
      videoSessionId: null,
      calibration: new PointerCalibration(),
    };
    this.browsers.set(sessionId, conn);
    let set = this.browsersByUser.get(userId);
    if (!set) {
      set = new Set();
      this.browsersByUser.set(userId, set);
    }
    set.add(conn);

    this.send(transport, { type: "welcome", sessionId, userId });
    this.send(transport, { type: "devices", devices: this.summariesFor(userId, sessionId) });
    this.log.info({ sessionId, userId }, "browser connected");
  }

  /**
   * Handle a browser socket closing. Requirement 12: if it was controlling a
   * device, tell that device to release all HID state.
   */
  removeBrowser(sessionId: string): void {
    const conn = this.browsers.get(sessionId);
    if (!conn) return;

    if (conn.controllingDeviceId) {
      this.releaseDevice(conn, "browser disconnected");
    }
    if (conn.videoDeviceId) {
      this.endVideoSubscription(conn, "browser disconnected");
    }

    this.browsers.delete(sessionId);
    this.browsersByUser.get(conn.userId)?.delete(conn);
    this.log.info({ sessionId, userId: conn.userId }, "browser disconnected");
  }

  handleBrowserMessage(sessionId: string, msg: BrowserToServer): void {
    const conn = this.browsers.get(sessionId);
    if (!conn) return;

    switch (msg.type) {
      case "claim":
        this.handleClaim(conn, msg.deviceId);
        break;
      case "release":
        if (conn.controllingDeviceId) {
          const deviceId = conn.controllingDeviceId;
          this.releaseDevice(conn, "released by operator");
          this.send(conn.transport, { type: "released", deviceId });
        }
        break;
      case "release_all":
        this.handleReleaseAll(conn);
        break;
      case "input":
        this.handleInput(conn, msg.seq, msg.ts, msg.event);
        break;
      case "tap_normalized":
        this.handleTapNormalized(conn, msg.seq, msg.ts, msg.x, msg.y);
        break;
      case "calibrate_pointer":
        this.handleCalibratePointer(conn);
        break;
      case "video_subscribe":
        this.handleVideoSubscribe(conn, msg.deviceId);
        break;
      case "webrtc_offer":
      case "webrtc_answer":
      case "ice_candidate":
        this.handleBrowserWebrtc(conn, msg);
        break;
      case "ping":
        this.send(conn.transport, { type: "pong", ts: msg.ts });
        break;
      default:
        this.send(conn.transport, { type: "error", reason: "unknown_message_type" });
    }
  }

  private handleClaim(conn: BrowserConn, deviceId: string): void {
    const record = this.deviceStore.getDevice(deviceId);
    if (!record || record.ownerId !== conn.userId) {
      this.log.warn(
        { sessionId: conn.sessionId, userId: conn.userId, deviceId },
        "claim denied: not owner",
      );
      this.send(conn.transport, { type: "claim_failed", deviceId, reason: "not_found" });
      return;
    }

    const device = this.devices.get(deviceId);
    if (!device || !device.online) {
      this.send(conn.transport, { type: "claim_failed", deviceId, reason: "offline" });
      return;
    }

    if (device.controllingSessionId && device.controllingSessionId !== conn.sessionId) {
      this.send(conn.transport, { type: "claim_failed", deviceId, reason: "busy" });
      return;
    }

    if (conn.controllingDeviceId && conn.controllingDeviceId !== deviceId) {
      this.releaseDevice(conn, "switched device");
    }

    if (device.controllingSessionId === conn.sessionId) {
      this.send(device.transport, { type: "release_all" });
    }

    const controlSessionId = randomBytes(9).toString("base64url");
    device.controllingSessionId = conn.sessionId;
    conn.controllingDeviceId = deviceId;
    conn.controlSessionId = controlSessionId;
    conn.seq = new SequenceTracker(this.staleMs);
    conn.deviceOutSeq = 0;
    conn.calibration = new PointerCalibration();

    this.send(conn.transport, { type: "claimed", deviceId, controlSessionId });
    this.send(conn.transport, { type: "calibration_state", state: conn.calibration.state });
    this.log.info(
      { sessionId: conn.sessionId, deviceId, controlSessionId },
      "control session begin",
    );
    this.notifyOwnerDevices(record.ownerId);
  }

  private handleInput(conn: BrowserConn, seq: number, ts: number, event: InputEvent): void {
    const deviceId = conn.controllingDeviceId;
    if (!deviceId || !conn.seq) {
      this.send(conn.transport, { type: "error", reason: "not_controlling" });
      return;
    }
    const device = this.devices.get(deviceId);
    if (!device || !device.online || device.controllingSessionId !== conn.sessionId) {
      this.send(conn.transport, { type: "error", reason: "not_controlling" });
      return;
    }

    const gate = conn.seq.accept(seq, ts, this.now());
    if (!gate.ok) {
      this.log.debug(
        { sessionId: conn.sessionId, deviceId, seq, reason: gate.reason },
        "input dropped",
      );
      return;
    }

    this.forwardHid(conn, device, event);
  }

  private handleCalibratePointer(conn: BrowserConn): void {
    const deviceId = conn.controllingDeviceId;
    if (!deviceId || !conn.seq) {
      this.send(conn.transport, { type: "error", reason: "not_controlling" });
      return;
    }
    const device = this.devices.get(deviceId);
    if (!device || !device.online || device.controllingSessionId !== conn.sessionId) {
      this.send(conn.transport, { type: "error", reason: "not_controlling" });
      return;
    }

    this.send(conn.transport, { type: "calibration_state", state: "CALIBRATING" });
    const events = conn.calibration.beginCalibrate();
    for (const event of events) {
      this.forwardHid(conn, device, event);
    }
    this.send(conn.transport, { type: "calibration_state", state: conn.calibration.state });
    this.log.info({ sessionId: conn.sessionId, deviceId }, "pointer calibrated");
  }

  private handleTapNormalized(
    conn: BrowserConn,
    seq: number,
    ts: number,
    x: number,
    y: number,
  ): void {
    const deviceId = conn.controllingDeviceId;
    if (!deviceId || !conn.seq) {
      this.send(conn.transport, { type: "error", reason: "not_controlling" });
      return;
    }
    const device = this.devices.get(deviceId);
    if (!device || !device.online || device.controllingSessionId !== conn.sessionId) {
      this.send(conn.transport, { type: "error", reason: "not_controlling" });
      return;
    }

    const gate = conn.seq.accept(seq, ts, this.now());
    if (!gate.ok) {
      this.log.debug(
        { sessionId: conn.sessionId, deviceId, seq, reason: gate.reason },
        "tap_normalized dropped",
      );
      return;
    }

    if (conn.calibration.state !== "READY") {
      this.send(conn.transport, { type: "error", reason: "pointer_not_calibrated" });
      this.send(conn.transport, {
        type: "calibration_state",
        state: conn.calibration.state,
      });
      return;
    }

    const events = conn.calibration.planTap(x, y);
    if (events.length === 0) {
      this.send(conn.transport, { type: "error", reason: "invalid_tap" });
      return;
    }
    for (const event of events) {
      this.forwardHid(conn, device, event);
    }
  }

  /** Emit one HID input to the device with the next outbound session seq. */
  private forwardHid(conn: BrowserConn, device: DeviceConn, event: InputEvent): void {
    if (event.kind === "move") {
      conn.calibration.applyRelativeMove(event.dx, event.dy);
    }
    conn.deviceOutSeq += 1;
    this.send(device.transport, {
      type: "input",
      session: conn.controlSessionId ?? "",
      seq: conn.deviceOutSeq,
      event,
    });
  }

  private handleVideoSubscribe(conn: BrowserConn, deviceId: string): void {
    const record = this.deviceStore.getDevice(deviceId);
    if (!record || record.ownerId !== conn.userId) {
      this.send(conn.transport, { type: "error", reason: "not_found" });
      return;
    }

    if (conn.videoDeviceId && conn.videoDeviceId !== deviceId) {
      this.endVideoSubscription(conn, "switched video device");
    }

    const agent = this.videoAgents.get(deviceId);
    const videoSessionId = randomUUID();
    conn.videoDeviceId = deviceId;
    conn.videoSessionId = videoSessionId;

    if (agent) {
      agent.videoSessionId = videoSessionId;
      agent.streaming = false;
      agent.webRtcConnected = false;
      this.sendAgent(agent.transport, {
        type: "stream_start",
        sessionId: videoSessionId,
        deviceId,
      });
      this.log.info(
        { sessionId: conn.sessionId, deviceId, videoSessionId },
        "video session start",
      );
    }

    this.send(conn.transport, {
      type: "video_status",
      deviceId,
      videoAgentOnline: !!agent,
      videoStreaming: agent?.streaming ?? false,
      webRtcConnected: agent?.webRtcConnected ?? false,
      sessionId: videoSessionId,
    });
  }

  private endVideoSubscription(conn: BrowserConn, reason: string): void {
    const deviceId = conn.videoDeviceId;
    const videoSessionId = conn.videoSessionId;
    if (!deviceId) return;

    const agent = this.videoAgents.get(deviceId);
    if (agent && agent.videoSessionId === videoSessionId) {
      this.sendAgent(agent.transport, {
        type: "stream_stop",
        sessionId: videoSessionId ?? undefined,
        deviceId,
      });
      agent.videoSessionId = null;
      agent.streaming = false;
      agent.webRtcConnected = false;
      this.log.info({ deviceId, sessionId: videoSessionId, reason }, "video session end");
    }

    conn.videoDeviceId = null;
    conn.videoSessionId = null;
  }

  private handleBrowserWebrtc(
    conn: BrowserConn,
    msg: Extract<
      BrowserToServer,
      { type: "webrtc_offer" | "webrtc_answer" | "ice_candidate" }
    >,
  ): void {
    // Must own the device.
    const record = this.deviceStore.getDevice(msg.deviceId);
    if (!record || record.ownerId !== conn.userId) {
      this.send(conn.transport, { type: "error", reason: "not_found" });
      return;
    }

    // Must be controlling or video-subscribed to THIS device.
    const isController = conn.controllingDeviceId === msg.deviceId;
    const isSubscriber = conn.videoDeviceId === msg.deviceId;
    if (!isController && !isSubscriber) {
      this.send(conn.transport, { type: "error", reason: "not_subscribed" });
      return;
    }

    const agent = this.videoAgents.get(msg.deviceId);
    if (!agent) {
      this.send(conn.transport, { type: "error", reason: "video_agent_offline" });
      return;
    }

    // Session must match the active video session (stale rejection).
    const expected =
      conn.videoSessionId ??
      agent.videoSessionId;
    if (!expected || msg.sessionId !== expected) {
      this.send(conn.transport, { type: "error", reason: "stale_session" });
      return;
    }

    // Ensure agent latches the same session if subscribe already set it on the browser.
    if (!agent.videoSessionId) {
      agent.videoSessionId = msg.sessionId;
    }
    if (msg.sessionId !== agent.videoSessionId) {
      this.send(conn.transport, { type: "error", reason: "stale_session" });
      return;
    }

    const payload: ServerToAgent =
      msg.type === "ice_candidate"
        ? {
            type: "ice_candidate",
            deviceId: msg.deviceId,
            sessionId: msg.sessionId,
            candidate: msg.candidate,
            sdpMid: msg.sdpMid,
            sdpMLineIndex: msg.sdpMLineIndex,
          }
        : {
            type: msg.type,
            deviceId: msg.deviceId,
            sessionId: msg.sessionId,
            sdp: msg.sdp,
          };

    this.sendAgent(agent.transport, payload);
  }

  private handleReleaseAll(conn: BrowserConn): void {
    const deviceId = conn.controllingDeviceId;
    if (!deviceId) return;
    const device = this.devices.get(deviceId);
    if (device && device.online && device.controllingSessionId === conn.sessionId) {
      this.send(device.transport, { type: "release_all" });
      this.log.info({ sessionId: conn.sessionId, deviceId }, "emergency release_all");
    }
  }

  /** Clear a session's claim and tell the device to drop all HID state. */
  private releaseDevice(conn: BrowserConn, reason: string): void {
    const deviceId = conn.controllingDeviceId;
    if (!deviceId) return;
    const device = this.devices.get(deviceId);
    if (device && device.controllingSessionId === conn.sessionId) {
      device.controllingSessionId = null;
      if (device.online) {
        this.send(device.transport, { type: "release_all" });
      }
      this.notifyOwnerDevices(device.record.ownerId);
    }
    this.log.info(
      { sessionId: conn.sessionId, deviceId, reason },
      "control session end",
    );
    conn.controllingDeviceId = null;
    conn.controlSessionId = null;
    conn.seq = null;
    conn.deviceOutSeq = 0;
    conn.calibration.reset();
  }

  /* ---------------------------------------------------------------------- */
  /* Views / fan-out                                                        */
  /* ---------------------------------------------------------------------- */

  private summariesFor(userId: string, viewerSessionId: string): DeviceSummary[] {
    return this.deviceStore.listByOwner(userId).map((record) => {
      const conn = this.devices.get(record.id);
      const agent = this.videoAgents.get(record.id);
      const controllerOnline = conn?.online ?? false;
      const controller = conn?.controllingSessionId ?? null;
      return {
        id: record.id,
        name: record.name,
        online: controllerOnline,
        deviceOnline: controllerOnline,
        controllerOnline,
        videoAgentOnline: !!agent,
        videoStreaming: agent?.streaming ?? false,
        hidReady: controllerOnline,
        webRtcConnected: agent?.webRtcConnected ?? false,
        lastSeen: conn?.lastSeen ?? agent?.lastSeen ?? null,
        controlledByYou: controller === viewerSessionId,
        busy: controller !== null && controller !== viewerSessionId,
      };
    });
  }

  /** Push a fresh device list to every browser owned by `ownerId`. */
  private notifyOwnerDevices(ownerId: string): void {
    const set = this.browsersByUser.get(ownerId);
    if (!set) return;
    for (const b of set) {
      this.send(b.transport, { type: "devices", devices: this.summariesFor(ownerId, b.sessionId) });
    }
  }

  private notifyOwnerOf(deviceId: string): void {
    const record = this.deviceStore.getDevice(deviceId);
    if (record) this.notifyOwnerDevices(record.ownerId);
  }

  private broadcastVideoStatus(deviceId: string): void {
    const agent = this.videoAgents.get(deviceId);
    const payload: ServerToBrowser = {
      type: "video_status",
      deviceId,
      videoAgentOnline: !!agent,
      videoStreaming: agent?.streaming ?? false,
      webRtcConnected: agent?.webRtcConnected ?? false,
      sessionId: agent?.videoSessionId ?? undefined,
    };
    this.fanoutToVideoBrowsers(deviceId, payload);
  }

  /**
   * Fan-out to browsers that claimed or video-subscribed this device.
   * Optionally filter by video session id.
   */
  private fanoutToVideoBrowsers(
    deviceId: string,
    msg: ServerToBrowser,
    sessionId?: string,
  ): void {
    for (const browser of this.browsers.values()) {
      const watching =
        browser.controllingDeviceId === deviceId || browser.videoDeviceId === deviceId;
      if (!watching) continue;
      if (sessionId && browser.videoSessionId && browser.videoSessionId !== sessionId) {
        continue;
      }
      this.send(browser.transport, msg);
    }
  }

  private send(transport: Transport, msg: ServerToBrowser | ServerToDevice): void {
    try {
      transport.send(JSON.stringify(msg));
    } catch (err) {
      this.log.warn({ err: (err as Error).message }, "transport send failed");
    }
  }

  private sendAgent(transport: Transport, msg: ServerToAgent): void {
    try {
      transport.send(JSON.stringify(msg));
    } catch (err) {
      this.log.warn({ err: (err as Error).message }, "agent transport send failed");
    }
  }

  /* ---------------------------------------------------------------------- */
  /* Test / introspection helpers                                           */
  /* ---------------------------------------------------------------------- */

  /** @internal for tests: is a device currently controlled, and by whom. */
  getController(deviceId: string): string | null {
    return this.devices.get(deviceId)?.controllingSessionId ?? null;
  }

  /** @internal for tests. */
  isDeviceOnline(deviceId: string): boolean {
    return this.devices.get(deviceId)?.online ?? false;
  }

  /** @internal for tests. */
  isVideoAgentOnline(deviceId: string): boolean {
    return this.videoAgents.has(deviceId);
  }

  /** @internal for tests. */
  getVideoSessionId(deviceId: string): string | null {
    return this.videoAgents.get(deviceId)?.videoSessionId ?? null;
  }
}
