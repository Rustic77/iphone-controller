import { randomBytes } from "node:crypto";
import type { Logger } from "./logger.js";
import type { DeviceCredentialStore, DeviceRecord } from "./stores/deviceStore.js";
import { SequenceTracker } from "./sequenceTracker.js";
import type {
  BrowserToServer,
  DeviceSummary,
  DeviceToServer,
  InputEvent,
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

interface BrowserConn {
  sessionId: string;
  userId: string;
  transport: Transport;
  /** device this session currently controls, or null. */
  controllingDeviceId: string | null;
  /** id of the current control session (regenerated on each claim). */
  controlSessionId: string | null;
  seq: SequenceTracker | null;
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
 *   - a browser may only claim/control devices its user owns (tenant isolation)
 *   - input is routed ONLY to the device the session currently controls
 *   - sequence + staleness gating drops duplicate/old commands
 */
export class Hub {
  private readonly devices = new Map<string, DeviceConn>();
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
  /* Devices                                                                */
  /* ---------------------------------------------------------------------- */

  /** Register an authenticated device socket as online. */
  addDevice(record: DeviceRecord, transport: Transport): void {
    const existing = this.devices.get(record.id);
    if (existing && existing.transport !== transport) {
      // Reconnect / duplicate: drop the stale socket, keep the newest.
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
    this.log.info({ deviceId: record.id, ownerId: record.ownerId }, "device online");
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
    this.log.info({ deviceId }, "device offline");

    // Requirement 13: any browser controlling it must immediately see offline.
    const controllingSession = conn.controllingSessionId;
    if (controllingSession) {
      const browser = this.browsers.get(controllingSession);
      if (browser && browser.controllingDeviceId === deviceId) {
        browser.controllingDeviceId = null;
        browser.controlSessionId = null;
        browser.seq = null;
        this.send(browser.transport, { type: "device_status", deviceId, online: false });
        this.send(browser.transport, { type: "released", deviceId });
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
      case "ping":
        this.send(conn.transport, { type: "pong", ts: msg.ts });
        break;
      default:
        this.send(conn.transport, { type: "error", reason: "unknown_message_type" });
    }
  }

  private handleClaim(conn: BrowserConn, deviceId: string): void {
    // Ownership check FIRST — this is the tenant-isolation boundary. A session
    // may only ever reference a device its user owns.
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

    // Release any device this session previously controlled.
    if (conn.controllingDeviceId && conn.controllingDeviceId !== deviceId) {
      this.releaseDevice(conn, "switched device");
    }

    // If this session is re-claiming a device it already held, the device still
    // has the old control-session latched. Tell it to release so it cleanly
    // adopts the new controlSessionId below (every session change on the device
    // is preceded by a release_all — see the device protocol).
    if (device.controllingSessionId === conn.sessionId) {
      this.send(device.transport, { type: "release_all" });
    }

    const controlSessionId = randomBytes(9).toString("base64url");
    device.controllingSessionId = conn.sessionId;
    conn.controllingDeviceId = deviceId;
    conn.controlSessionId = controlSessionId;
    conn.seq = new SequenceTracker(this.staleMs);

    this.send(conn.transport, { type: "claimed", deviceId, controlSessionId });
    this.log.info(
      { sessionId: conn.sessionId, deviceId, controlSessionId },
      "device claimed",
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
    // Requirement 8: never route to a device this session does not control.
    if (!device || !device.online || device.controllingSessionId !== conn.sessionId) {
      this.send(conn.transport, { type: "error", reason: "not_controlling" });
      return;
    }

    const gate = conn.seq.accept(seq, ts, this.now());
    if (!gate.ok) {
      // Duplicate/stale/bad — drop silently but record for diagnostics.
      this.log.debug(
        { sessionId: conn.sessionId, deviceId, seq, reason: gate.reason },
        "input dropped",
      );
      return;
    }

    this.send(device.transport, {
      type: "input",
      session: conn.controlSessionId ?? "",
      seq,
      event,
    });
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
    conn.controllingDeviceId = null;
    conn.controlSessionId = null;
    conn.seq = null;
    this.log.info({ sessionId: conn.sessionId, deviceId, reason }, "device released");
  }

  /* ---------------------------------------------------------------------- */
  /* Views / fan-out                                                        */
  /* ---------------------------------------------------------------------- */

  private summariesFor(userId: string, viewerSessionId: string): DeviceSummary[] {
    return this.deviceStore.listByOwner(userId).map((record) => {
      const conn = this.devices.get(record.id);
      const online = conn?.online ?? false;
      const controller = conn?.controllingSessionId ?? null;
      return {
        id: record.id,
        name: record.name,
        online,
        lastSeen: conn?.lastSeen ?? null,
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

  private send(transport: Transport, msg: ServerToBrowser | ServerToDevice): void {
    try {
      transport.send(JSON.stringify(msg));
    } catch (err) {
      this.log.warn({ err: (err as Error).message }, "transport send failed");
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
}
