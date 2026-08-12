import { describe, it, expect } from "vitest";
import { makeHub, connectDevice, FakeTransport } from "./helpers.js";

describe("session isolation (tenant boundary)", () => {
  it("lets an owner claim their own online device", () => {
    const { hub, deviceStore } = makeHub();
    connectDevice(hub, deviceStore, "devA");

    const browser = new FakeTransport();
    hub.addBrowser("clientA", "userA", browser);
    hub.handleBrowserMessage("clientA", { type: "claim", deviceId: "devA" });

    expect(browser.ofType("claimed")).toHaveLength(1);
    expect(hub.getController("devA")).toBe("clientA");
  });

  it("does NOT let a user claim another user's device", () => {
    const { hub, deviceStore } = makeHub();
    connectDevice(hub, deviceStore, "devA"); // owned by userA

    const attacker = new FakeTransport();
    hub.addBrowser("clientB", "userB", attacker);
    hub.handleBrowserMessage("clientB", { type: "claim", deviceId: "devA" });

    // Existence of a device the attacker doesn't own is not even revealed.
    const failed = attacker.ofType("claim_failed");
    expect(failed).toHaveLength(1);
    expect(failed[0].reason).toBe("not_found");
    expect(hub.getController("devA")).toBeNull();
  });

  it("never routes one user's input to another user's device", () => {
    const { hub, deviceStore } = makeHub();
    const deviceA = connectDevice(hub, deviceStore, "devA"); // userA's device

    const attacker = new FakeTransport();
    hub.addBrowser("clientB", "userB", attacker);
    // Attacker never successfully claimed devA, so sending input must not reach it.
    hub.handleBrowserMessage("clientB", {
      type: "input",
      seq: 1,
      ts: 1_000_000,
      event: { kind: "move", dx: 5, dy: 5 },
    });

    expect(deviceA.ofType("input")).toHaveLength(0);
    expect(attacker.ofType("error").map((m) => m.reason)).toContain("not_controlling");
  });

  it("only one session controls a device at a time (busy)", () => {
    const { hub, deviceStore } = makeHub();
    connectDevice(hub, deviceStore, "devA");

    const first = new FakeTransport();
    hub.addBrowser("c1", "userA", first);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });

    const second = new FakeTransport();
    hub.addBrowser("c2", "userA", second);
    hub.handleBrowserMessage("c2", { type: "claim", deviceId: "devA" });

    expect(second.ofType("claim_failed")[0].reason).toBe("busy");
    expect(hub.getController("devA")).toBe("c1");
  });

  it("a device list only shows devices the operator owns", () => {
    const { hub } = makeHub();
    const browser = new FakeTransport();
    hub.addBrowser("clientA", "userA", browser);
    const devices = browser.ofType("devices").at(-1).devices;
    expect(devices.map((d: any) => d.id)).toEqual(["devA"]);
  });
});
