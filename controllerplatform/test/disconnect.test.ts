import { describe, it, expect } from "vitest";
import { makeHub, connectDevice, FakeTransport } from "./helpers.js";

describe("disconnect & release behavior", () => {
  it("tells the device to release all HID state when the browser disconnects", () => {
    const { hub, deviceStore } = makeHub();
    const device = connectDevice(hub, deviceStore, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });

    hub.removeBrowser("c1"); // browser disconnects

    expect(device.ofType("release_all")).toHaveLength(1);
    expect(hub.getController("devA")).toBeNull();
  });

  it("emergency RELEASE ALL forwards to the controlled device without releasing the claim", () => {
    const { hub, deviceStore } = makeHub();
    const device = connectDevice(hub, deviceStore, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });

    hub.handleBrowserMessage("c1", { type: "release_all" });

    expect(device.ofType("release_all")).toHaveLength(1);
    // still controlling — operator can keep working after an emergency stop
    expect(hub.getController("devA")).toBe("c1");
  });

  it("explicit release drops the claim and tells the device to release HID state", () => {
    const { hub, deviceStore } = makeHub();
    const device = connectDevice(hub, deviceStore, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });

    hub.handleBrowserMessage("c1", { type: "release" });

    expect(device.ofType("release_all")).toHaveLength(1);
    expect(browser.ofType("released")).toHaveLength(1);
    expect(hub.getController("devA")).toBeNull();
  });

  it("notifies the controlling browser immediately when the device goes offline", () => {
    const { hub, deviceStore } = makeHub();
    const device = connectDevice(hub, deviceStore, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });
    browser.clear();

    hub.removeDevice("devA", device); // device disconnects

    const statusMsgs = browser.ofType("device_status");
    expect(statusMsgs.some((m) => m.deviceId === "devA" && m.online === false)).toBe(true);
    expect(hub.isDeviceOnline("devA")).toBe(false);
    // The claim is cleared so the operator can't keep sending into the void.
    expect(hub.getController("devA")).toBeNull();
  });

  it("reflects offline state in the device list pushed to browsers", () => {
    const { hub, deviceStore } = makeHub();
    const device = connectDevice(hub, deviceStore, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    browser.clear();

    hub.removeDevice("devA", device);

    const latest = browser.ofType("devices").at(-1).devices;
    const devA = latest.find((d: any) => d.id === "devA");
    expect(devA.online).toBe(false);
  });

  it("input after device disconnect is not forwarded and errors back to the browser", () => {
    const { hub, deviceStore, clock } = makeHub();
    const device = connectDevice(hub, deviceStore, "devA");
    const browser = new FakeTransport();
    hub.addBrowser("c1", "userA", browser);
    hub.handleBrowserMessage("c1", { type: "claim", deviceId: "devA" });
    hub.removeDevice("devA", device);
    browser.clear();

    hub.handleBrowserMessage("c1", {
      type: "input",
      seq: 1,
      ts: clock.now(),
      event: { kind: "move", dx: 1, dy: 1 },
    });
    expect(browser.ofType("error").map((m) => m.reason)).toContain("not_controlling");
  });
});
