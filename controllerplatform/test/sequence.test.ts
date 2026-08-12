import { describe, it, expect } from "vitest";
import { SequenceTracker } from "../src/sequenceTracker.js";
import { makeHub, connectDevice, FakeTransport } from "./helpers.js";

describe("SequenceTracker", () => {
  it("accepts strictly increasing sequence numbers", () => {
    const t = new SequenceTracker(10_000);
    expect(t.accept(1, 0, 0).ok).toBe(true);
    expect(t.accept(2, 0, 0).ok).toBe(true);
    expect(t.accept(3, 0, 0).ok).toBe(true);
    expect(t.lastAcceptedSeq).toBe(3);
  });

  it("drops duplicates (same seq)", () => {
    const t = new SequenceTracker(10_000);
    t.accept(5, 0, 0);
    const r = t.accept(5, 0, 0);
    expect(r.ok).toBe(false);
    expect(r.ok === false && r.reason).toBe("duplicate");
  });

  it("drops out-of-order / older sequence numbers", () => {
    const t = new SequenceTracker(10_000);
    t.accept(10, 0, 0);
    const r = t.accept(4, 0, 0);
    expect(r.ok).toBe(false);
    expect(r.ok === false && r.reason).toBe("duplicate");
  });

  it("rejects non-finite seq/ts", () => {
    const t = new SequenceTracker(10_000);
    expect(t.accept(NaN, 0, 0).ok).toBe(false);
    expect(t.accept(1, NaN, 0).ok).toBe(false);
  });
});

describe("Hub sequence handling", () => {
  it("forwards only accepted, in-order input to the device", () => {
    const { hub, deviceStore, clock } = makeHub();
    const device = connectDevice(hub, deviceStore, "devA");

    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });

    const now = clock.now();
    hub.handleBrowserMessage("c1", { type: "input", seq: 1, ts: now, event: { kind: "move", dx: 1, dy: 0 } });
    hub.handleBrowserMessage("c1", { type: "input", seq: 2, ts: now, event: { kind: "move", dx: 2, dy: 0 } });
    // duplicate of seq 2 — must be dropped
    hub.handleBrowserMessage("c1", { type: "input", seq: 2, ts: now, event: { kind: "move", dx: 9, dy: 9 } });
    // older seq — must be dropped
    hub.handleBrowserMessage("c1", { type: "input", seq: 1, ts: now, event: { kind: "move", dx: 9, dy: 9 } });
    hub.handleBrowserMessage("c1", { type: "input", seq: 3, ts: now, event: { kind: "move", dx: 3, dy: 0 } });

    const forwarded = device.ofType("input");
    expect(forwarded.map((m) => m.seq)).toEqual([1, 2, 3]);
    expect(forwarded.map((m) => m.event.dx)).toEqual([1, 2, 3]);
  });

  it("gives each fresh claim its own sequence space", () => {
    const { hub, deviceStore, clock } = makeHub();
    const device = connectDevice(hub, deviceStore, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);

    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });
    hub.handleBrowserMessage("c1", { type: "input", seq: 100, ts: clock.now(), event: { kind: "move", dx: 1, dy: 0 } });
    hub.handleBrowserMessage("c1", { type: "release" });

    // Re-claim: sequence counter resets, so low seq numbers are accepted again.
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });
    hub.handleBrowserMessage("c1", { type: "input", seq: 1, ts: clock.now(), event: { kind: "move", dx: 7, dy: 0 } });

    const forwarded = device.ofType("input");
    expect(forwarded.map((m) => m.seq)).toEqual([100, 1]);
  });

  it("stamps forwarded input with the control-session id, which changes per claim", () => {
    const { hub, deviceStore, clock } = makeHub();
    const device = connectDevice(hub, deviceStore, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);

    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });
    const s1 = browser.ofType("claimed").at(-1).controlSessionId;
    hub.handleBrowserMessage("c1", { type: "input", seq: 1, ts: clock.now(), event: { kind: "move", dx: 1, dy: 0 } });

    hub.handleBrowserMessage("c1", { type: "release" });
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });
    const s2 = browser.ofType("claimed").at(-1).controlSessionId;
    hub.handleBrowserMessage("c1", { type: "input", seq: 1, ts: clock.now(), event: { kind: "move", dx: 2, dy: 0 } });

    const forwarded = device.ofType("input");
    expect(forwarded[0].session).toBe(s1);
    expect(forwarded[1].session).toBe(s2);
    expect(s1).not.toBe(s2);
  });
});
