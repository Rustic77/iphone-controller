import { describe, it, expect } from "vitest";
import { SequenceTracker } from "../src/sequenceTracker.js";
import { makeHub, connectDevice, FakeTransport } from "./helpers.js";

describe("stale command rejection", () => {
  it("rejects a command older than the stale window", () => {
    const t = new SequenceTracker(2000);
    const now = 100_000;
    // ts is 2500ms old -> stale
    const r = t.accept(1, now - 2500, now);
    expect(r.ok).toBe(false);
    expect(r.ok === false && r.reason).toBe("stale");
  });

  it("accepts a command within the stale window", () => {
    const t = new SequenceTracker(2000);
    const now = 100_000;
    expect(t.accept(1, now - 1500, now).ok).toBe(true);
  });

  it("does not advance lastSeq when a command is rejected as stale", () => {
    const t = new SequenceTracker(2000);
    const now = 100_000;
    t.accept(5, now - 5000, now); // stale, dropped
    expect(t.lastAcceptedSeq).toBe(-Infinity);
    // a fresh command with a lower-ish seq is still accepted
    expect(t.accept(5, now, now).ok).toBe(true);
  });

  it("Hub drops stale input and does not forward it to the device", () => {
    const staleMs = 2000;
    const { hub, deviceStore, clock } = makeHub(staleMs);
    const device = connectDevice(hub, deviceStore, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });

    const oldTs = clock.now();
    clock.advance(5000); // 5s later — anything stamped oldTs is now stale

    hub.handleBrowserMessage("c1", {
      type: "input",
      seq: 1,
      ts: oldTs,
      event: { kind: "move", dx: 1, dy: 0 },
    });
    expect(device.ofType("input")).toHaveLength(0);

    // A fresh command with the current clock is forwarded.
    hub.handleBrowserMessage("c1", {
      type: "input",
      seq: 2,
      ts: clock.now(),
      event: { kind: "move", dx: 2, dy: 0 },
    });
    expect(device.ofType("input")).toHaveLength(1);
  });
});
