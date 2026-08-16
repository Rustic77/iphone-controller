import { describe, it, expect } from "vitest";
import { makeHub, connectDevice, FakeTransport } from "./helpers.js";

function connectAgent(hub: ReturnType<typeof makeHub>["hub"], deviceId: string, agentId = "agent-1") {
  const t = new FakeTransport();
  hub.addVideoAgent(deviceId, agentId, t);
  return t;
}

describe("HID independence from video", () => {
  it("forwards move, click, and scroll to the ESP while a video session is streaming", () => {
    const { hub, deviceStore, clock } = makeHub();
    const device = connectDevice(hub, deviceStore, "devA");
    connectAgent(hub, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });
    device.clear();

    const now = clock.now();
    hub.handleBrowserMessage("c1", {
      type: "input",
      seq: 1,
      ts: now,
      event: { kind: "move", dx: 4, dy: -2 },
    });
    hub.handleBrowserMessage("c1", {
      type: "input",
      seq: 2,
      ts: now,
      event: { kind: "click", button: "left", pressed: true },
    });
    hub.handleBrowserMessage("c1", {
      type: "input",
      seq: 3,
      ts: now,
      event: { kind: "click", button: "left", pressed: false },
    });
    hub.handleBrowserMessage("c1", {
      type: "input",
      seq: 4,
      ts: now,
      event: { kind: "scroll", dx: 0, dy: 3 },
    });

    expect(device.ofType("input").map((m) => m.event)).toEqual([
      { kind: "move", dx: 4, dy: -2 },
      { kind: "click", button: "left", pressed: true },
      { kind: "click", button: "left", pressed: false },
      { kind: "scroll", dx: 0, dy: 3 },
    ]);
  });
});

describe("device list presence", () => {
  it("lists owned devices even when ESP and video agent are both offline", () => {
    const { hub } = makeHub();
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    const devices = browser.ofType("devices").at(-1).devices;
    expect(devices).toHaveLength(1);
    expect(devices[0].id).toBe("devA");
    expect(devices[0].controllerOnline).toBe(false);
    expect(devices[0].videoAgentOnline).toBe(false);
    expect(devices[0].hidReady).toBe(false);
  });

  it("marks the rig live when the video agent connects without an ESP", () => {
    const { hub } = makeHub();
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    connectAgent(hub, "devA");
    const d = browser.ofType("devices").at(-1).devices.find((x: { id: string }) => x.id === "devA");
    expect(d.videoAgentOnline).toBe(true);
    expect(d.controllerOnline).toBe(false);
  });
});

describe("claim auto-subscribes video", () => {
  it("mints a video session and sends stream_start on HID claim", () => {
    const { hub, deviceStore } = makeHub();
    connectDevice(hub, deviceStore, "devA");
    const agent = connectAgent(hub, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });

    expect(browser.ofType("claimed")).toHaveLength(1);
    const status = browser.ofType("video_status").at(-1);
    expect(status.sessionId).toBeTruthy();
    expect(agent.ofType("stream_start").some((m: { sessionId: string }) => m.sessionId === status.sessionId)).toBe(
      true,
    );
  });
});

describe("video session resume", () => {
  it("sends stream_start when the agent connects after the browser already subscribed", () => {
    const { hub, deviceStore } = makeHub();
    connectDevice(hub, deviceStore, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });

    const sessionId = browser.ofType("video_status").at(-1).sessionId as string;
    expect(sessionId).toBeTruthy();

    const agent = new FakeTransport();
    hub.addVideoAgent("devA", "agent-late", agent);

    expect(agent.ofType("stream_start").some((m: { sessionId: string }) => m.sessionId === sessionId)).toBe(
      true,
    );
    const resumed = browser.ofType("video_status").at(-1);
    expect(resumed.videoAgentOnline).toBe(true);
    expect(resumed.sessionId).toBe(sessionId);
  });
});

describe("video agent online flags", () => {
  it("reports videoAgentOnline independently of ESP controller", () => {
    const { hub } = makeHub();
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    browser.clear();

    // Agent online, ESP offline.
    connectAgent(hub, "devA");
    const withAgent = browser.ofType("devices").at(-1).devices.find((d: any) => d.id === "devA");
    expect(withAgent.videoAgentOnline).toBe(true);
    expect(withAgent.controllerOnline).toBe(false);
    expect(withAgent.online).toBe(false);
    expect(withAgent.deviceOnline).toBe(false);
    expect(withAgent.hidReady).toBe(false);
  });

  it("sets controller and video flags when both are connected", () => {
    const { hub, deviceStore } = makeHub();
    connectDevice(hub, deviceStore, "devA");
    connectAgent(hub, "devA");

    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    const d = browser.ofType("devices").at(-1).devices.find((x: any) => x.id === "devA");
    expect(d.controllerOnline).toBe(true);
    expect(d.online).toBe(true);
    expect(d.deviceOnline).toBe(true);
    expect(d.videoAgentOnline).toBe(true);
    expect(d.hidReady).toBe(true);
    expect(hub.isVideoAgentOnline("devA")).toBe(true);
  });

  it("clears videoAgentOnline on agent disconnect without releasing HID claim", () => {
    const { hub, deviceStore } = makeHub();
    connectDevice(hub, deviceStore, "devA");
    const agent = connectAgent(hub, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });
    expect(hub.getController("devA")).toBe("c1");

    hub.removeVideoAgent("devA", agent);
    expect(hub.isVideoAgentOnline("devA")).toBe(false);
    expect(hub.getController("devA")).toBe("c1");

    const d = browser.ofType("devices").at(-1).devices.find((x: any) => x.id === "devA");
    expect(d.videoAgentOnline).toBe(false);
    expect(d.controllerOnline).toBe(true);
  });
});

describe("WebRTC signaling isolation", () => {
  it("relays webrtc messages only for the subscribed device", () => {
    const { hub, deviceStore } = makeHub();
    connectDevice(hub, deviceStore, "devA");
    const agentA = connectAgent(hub, "devA", "agentA");

    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });

    const status = browser.ofType("video_status").at(-1);
    expect(status.deviceId).toBe("devA");
    expect(status.sessionId).toBeTruthy();
    const sessionId = status.sessionId as string;

    // stream_start went to agent
    expect(agentA.ofType("stream_start").some((m) => m.sessionId === sessionId)).toBe(true);

    browser.clear();
    agentA.clear();

    hub.handleBrowserMessage("c1", {
      type: "webrtc_offer",
      deviceId: "devA",
      sessionId,
      sdp: "v=0-offer-a",
    });
    expect(agentA.ofType("webrtc_offer")).toHaveLength(1);
    expect(agentA.ofType("webrtc_offer")[0].sdp).toBe("v=0-offer-a");
  });

  it("rejects webrtc for a device the browser does not own / is not subscribed to", () => {
    const { hub, deviceStore } = makeHub();
    connectDevice(hub, deviceStore, "devA");
    connectDevice(hub, deviceStore, "devB");
    connectAgent(hub, "devA");
    const agentB = connectAgent(hub, "devB", "agentB");

    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    // userA owns only devA
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });
    const sessionId = browser.ofType("video_status").at(-1).sessionId as string;

    browser.clear();
    agentB.clear();

    hub.handleBrowserMessage("c1", {
      type: "webrtc_offer",
      deviceId: "devB",
      sessionId,
      sdp: "v=0-leak",
    });

    expect(agentB.ofType("webrtc_offer")).toHaveLength(0);
    expect(browser.ofType("error").map((m) => m.reason)).toContain("not_found");
  });

  it("rejects stale sessionId on webrtc messages", () => {
    const { hub, deviceStore } = makeHub();
    connectDevice(hub, deviceStore, "devA");
    const agent = connectAgent(hub, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });

    browser.clear();
    agent.clear();

    hub.handleBrowserMessage("c1", {
      type: "webrtc_offer",
      deviceId: "devA",
      sessionId: "not-the-real-session",
      sdp: "v=0-stale",
    });

    expect(agent.ofType("webrtc_offer")).toHaveLength(0);
    expect(browser.ofType("error").map((m) => m.reason)).toContain("stale_session");
  });

  it("rejects agent webrtc with stale session", () => {
    const { hub, deviceStore } = makeHub();
    connectDevice(hub, deviceStore, "devA");
    const agent = connectAgent(hub, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });
    const sessionId = browser.ofType("video_status").at(-1).sessionId as string;

    browser.clear();
    hub.handleAgentMessage("devA", agent, {
      type: "webrtc_answer",
      sessionId: "old-session",
      sdp: "v=0-bad",
    });
    expect(browser.ofType("webrtc_answer")).toHaveLength(0);
    expect(agent.ofType("error").map((m) => m.reason)).toContain("stale_session");

    hub.handleAgentMessage("devA", agent, {
      type: "webrtc_answer",
      sessionId,
      sdp: "v=0-ok",
    });
    expect(browser.ofType("webrtc_answer")).toHaveLength(1);
  });
});

describe("pointer calibrate via hub", () => {
  it("calibrate_pointer sends home moves then READY", () => {
    const { hub, deviceStore } = makeHub();
    const device = connectDevice(hub, deviceStore, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });
    device.clear();
    browser.clear();

    hub.handleBrowserMessage("c1", { type: "calibrate_pointer" });

    const moves = device.ofType("input").filter((m) => m.event.kind === "move");
    expect(moves.length).toBe(100);
    expect(moves.every((m) => m.event.dx === -40 && m.event.dy === -40)).toBe(true);
    expect(browser.ofType("calibration_state").map((m) => m.state)).toContain("READY");
  });

  it("tap_normalized after calibrate forwards chunked moves and click", () => {
    const { hub, deviceStore, clock } = makeHub();
    const device = connectDevice(hub, deviceStore, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });
    hub.handleBrowserMessage("c1", { type: "calibrate_pointer" });
    device.clear();

    hub.handleBrowserMessage("c1", {
      type: "tap_normalized",
      seq: 1,
      ts: clock.now(),
      x: 0.1,
      y: 0.1,
    });

    const inputs = device.ofType("input");
    expect(inputs.length).toBeGreaterThan(2);
    expect(inputs.at(-2).event).toEqual({ kind: "click", button: "left", pressed: true });
    expect(inputs.at(-1).event).toEqual({ kind: "click", button: "left", pressed: false });
  });
});
