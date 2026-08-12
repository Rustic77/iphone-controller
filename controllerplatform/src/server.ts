import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { randomUUID } from "node:crypto";
import type { IncomingMessage } from "node:http";
import Fastify, { type FastifyInstance } from "fastify";
import fastifyStatic from "@fastify/static";
import { WebSocketServer, type WebSocket } from "ws";
import type { Config } from "./config.js";
import { SessionManager } from "./auth.js";
import { Hub } from "./hub.js";
import type { UserStore } from "./stores/userStore.js";
import type { DeviceCredentialStore, DeviceRecord } from "./stores/deviceStore.js";
import type { AgentToServer, BrowserToServer, DeviceToServer, Transport } from "./types.js";

const __dirname = dirname(fileURLToPath(import.meta.url));

export interface BuildServerDeps {
  config: Config;
  userStore: UserStore;
  deviceStore: DeviceCredentialStore;
}

/** Wrap a raw ws socket in the Hub's Transport interface. */
function transportFor(socket: WebSocket): Transport {
  return {
    send: (data) => {
      if (socket.readyState === socket.OPEN) socket.send(data);
    },
    close: (code, reason) => socket.close(code, reason),
  };
}

function firstHeader(req: IncomingMessage, name: string): string | undefined {
  const v = req.headers[name];
  return Array.isArray(v) ? v[0] : v;
}

/**
 * Normalize agent wire types. The Windows agent historically used PascalCase
 * (`WebrtcOffer`); the relay canonical form is snake_case (`webrtc_offer`).
 */
export function normalizeAgentMessage(raw: Record<string, unknown>): AgentToServer | null {
  const t = raw.type;
  if (typeof t !== "string") return null;

  const typeMap: Record<string, AgentToServer["type"]> = {
    register: "register",
    AgentRegister: "register",
    heartbeat: "heartbeat",
    Heartbeat: "heartbeat",
    webrtc_offer: "webrtc_offer",
    WebrtcOffer: "webrtc_offer",
    webrtc_answer: "webrtc_answer",
    WebrtcAnswer: "webrtc_answer",
    ice_candidate: "ice_candidate",
    IceCandidate: "ice_candidate",
    video_metadata: "video_metadata",
    VideoMetadata: "video_metadata",
    source_lost: "source_lost",
    SourceLost: "source_lost",
    stream_state: "stream_state",
    StreamState: "stream_state",
    error: "error",
    Error: "error",
  };

  const type = typeMap[t];
  if (!type) return null;

  const base = { ...raw, type } as Record<string, unknown>;

  // PascalCase agent payloads sometimes nest differently; flatten common fields.
  if (typeof base.sdpMid === "undefined" && typeof base.SdpMid === "string") {
    base.sdpMid = base.SdpMid;
  }
  if (typeof base.sdpMLineIndex === "undefined" && typeof base.SdpMLineIndex === "number") {
    base.sdpMLineIndex = base.SdpMLineIndex;
  }
  if (typeof base.candidate === "undefined" && typeof base.Candidate === "string") {
    base.candidate = base.Candidate;
  }
  if (typeof base.sdp === "undefined" && typeof base.Sdp === "string") {
    base.sdp = base.Sdp;
  }

  return base as unknown as AgentToServer;
}

export async function buildServer(deps: BuildServerDeps): Promise<FastifyInstance> {
  const { config, userStore, deviceStore } = deps;

  const app = Fastify({
    logger: {
      level: config.logLevel,
      // Structured JSON logs with a stable shape.
      base: { service: "control-relay" },
    },
  });

  const sessions = new SessionManager(config.serverSecret, config.sessionTtlMs);
  const hub = new Hub({
    deviceStore,
    logger: app.log,
    staleCommandMs: config.staleCommandMs,
  });

  // --- Static web UI -------------------------------------------------------
  await app.register(fastifyStatic, {
    root: join(__dirname, "..", "public"),
    prefix: "/",
  });

  // --- Auth: operator login ------------------------------------------------
  app.post("/api/login", async (request, reply) => {
    const body = (request.body ?? {}) as { username?: unknown; password?: unknown };
    if (typeof body.username !== "string" || typeof body.password !== "string") {
      return reply.code(400).send({ error: "username and password are required" });
    }
    const user = userStore.verifyLogin(body.username, body.password);
    if (!user) {
      request.log.warn({ username: body.username }, "login failed");
      return reply.code(401).send({ error: "invalid credentials" });
    }
    const { token, session } = sessions.issue(user.id);
    request.log.info({ userId: user.id, sessionId: session.id }, "login ok");
    return reply.send({ token, userId: user.id, expiresAt: session.expiresAt });
  });

  app.get("/api/health", async () => ({ ok: true }));

  // --- WebSocket layer -----------------------------------------------------
  // We attach `ws` directly to Fastify's HTTP server in noServer mode and route
  // the upgrade ourselves so device/agent auth can use request headers.
  const browserWss = new WebSocketServer({ noServer: true });
  const deviceWss = new WebSocketServer({ noServer: true });
  const agentWss = new WebSocketServer({ noServer: true });

  app.server.on("upgrade", (req, socket, head) => {
    let pathname: string;
    let query: URLSearchParams;
    try {
      const url = new URL(req.url ?? "/", "http://localhost");
      pathname = url.pathname;
      query = url.searchParams;
    } catch {
      socket.destroy();
      return;
    }

    if (pathname === "/ws/browser") {
      const session = sessions.verify(query.get("token"));
      if (!session) {
        socket.write("HTTP/1.1 401 Unauthorized\r\n\r\n");
        socket.destroy();
        app.log.warn({ path: pathname }, "browser ws rejected: bad token");
        return;
      }
      browserWss.handleUpgrade(req, socket, head, (ws) => {
        browserWss.emit("connection", ws, req, session);
      });
      return;
    }

    if (pathname === "/ws/device") {
      const deviceId = firstHeader(req, "x-device-id") ?? query.get("deviceId") ?? "";
      const secret = firstHeader(req, "x-device-secret") ?? query.get("secret") ?? "";
      const record = deviceStore.verifyDevice(deviceId, secret);
      if (!record) {
        socket.write("HTTP/1.1 401 Unauthorized\r\n\r\n");
        socket.destroy();
        app.log.warn({ path: pathname, deviceId }, "device ws rejected: bad credentials");
        return;
      }
      deviceWss.handleUpgrade(req, socket, head, (ws) => {
        deviceWss.emit("connection", ws, req, record);
      });
      return;
    }

    if (pathname === "/ws/agent") {
      const deviceId = firstHeader(req, "x-device-id") ?? query.get("deviceId") ?? "";
      const secret = firstHeader(req, "x-agent-secret") ?? query.get("secret") ?? "";
      const agentId = firstHeader(req, "x-agent-id") ?? query.get("agentId") ?? "";
      const record = deviceStore.verifyDevice(deviceId, secret);
      if (!record || !agentId) {
        socket.write("HTTP/1.1 401 Unauthorized\r\n\r\n");
        socket.destroy();
        app.log.warn(
          { path: pathname, deviceId, agentId: agentId || undefined },
          "agent ws rejected: bad credentials",
        );
        return;
      }
      agentWss.handleUpgrade(req, socket, head, (ws) => {
        agentWss.emit("connection", ws, req, { record, agentId });
      });
      return;
    }

    socket.destroy();
  });

  // --- Browser connections -------------------------------------------------
  browserWss.on("connection", (socket: WebSocket, _req: IncomingMessage, session: { id: string; userId: string }) => {
    const clientId = randomUUID();
    const transport = transportFor(socket);
    hub.addBrowser(clientId, session.userId, transport);
    attachHeartbeat(socket);

    socket.on("message", (raw) => {
      const msg = parse<BrowserToServer>(raw.toString());
      if (!msg) {
        transport.send(JSON.stringify({ type: "error", reason: "bad_json" }));
        return;
      }
      hub.handleBrowserMessage(clientId, msg);
    });

    socket.on("close", () => hub.removeBrowser(clientId));
    socket.on("error", () => hub.removeBrowser(clientId));
  });

  // --- Device connections --------------------------------------------------
  deviceWss.on("connection", (socket: WebSocket, _req: IncomingMessage, record: DeviceRecord) => {
    const transport = transportFor(socket);
    hub.addDevice(record, transport);
    attachHeartbeat(socket);

    socket.on("message", (raw) => {
      const msg = parse<DeviceToServer>(raw.toString());
      if (!msg) return;
      hub.handleDeviceMessage(record.id, transport, msg);
    });

    socket.on("close", () => hub.removeDevice(record.id, transport));
    socket.on("error", () => hub.removeDevice(record.id, transport));
  });

  // --- Video agent connections ---------------------------------------------
  agentWss.on(
    "connection",
    (
      socket: WebSocket,
      _req: IncomingMessage,
      meta: { record: DeviceRecord; agentId: string },
    ) => {
      const transport = transportFor(socket);
      hub.addVideoAgent(meta.record.id, meta.agentId, transport);
      attachHeartbeat(socket);

      socket.on("message", (raw) => {
        const parsed = parse<Record<string, unknown>>(raw.toString());
        if (!parsed) return;
        const msg = normalizeAgentMessage(parsed);
        if (!msg) return;
        hub.handleAgentMessage(meta.record.id, transport, msg);
      });

      socket.on("close", () => hub.removeVideoAgent(meta.record.id, transport));
      socket.on("error", () => hub.removeVideoAgent(meta.record.id, transport));
    },
  );

  // --- Heartbeat sweep -----------------------------------------------------
  // ws-level ping/pong. If a socket misses the timeout window it is terminated,
  // which triggers 'close' and the relevant Hub cleanup (offline / release_all).
  const liveness = new WeakMap<WebSocket, number>();
  function attachHeartbeat(socket: WebSocket): void {
    liveness.set(socket, Date.now());
    socket.on("pong", () => liveness.set(socket, Date.now()));
  }

  const sweep = setInterval(() => {
    const now = Date.now();
    for (const wss of [browserWss, deviceWss, agentWss]) {
      for (const socket of wss.clients) {
        const last = liveness.get(socket) ?? now;
        if (now - last > config.heartbeatTimeoutMs) {
          app.log.warn("heartbeat timeout; terminating socket");
          socket.terminate();
          continue;
        }
        if (socket.readyState === socket.OPEN) socket.ping();
      }
    }
  }, config.heartbeatIntervalMs);
  sweep.unref?.();

  app.addHook("onClose", async () => {
    clearInterval(sweep);
    browserWss.close();
    deviceWss.close();
    agentWss.close();
  });

  return app;
}

function parse<T>(raw: string): T | null {
  try {
    return JSON.parse(raw) as T;
  } catch {
    return null;
  }
}
