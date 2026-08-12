import { describe, it, expect } from "vitest";
import {
  PointerCalibration,
  chunkRelativeMove,
  HOME_STEPS,
  HOME_STEP,
  DEFAULT_PORTRAIT_WIDTH,
  DEFAULT_PORTRAIT_HEIGHT,
} from "../src/pointerCalibration.js";

describe("chunkRelativeMove", () => {
  it("splits large deltas into max-step chunks", () => {
    const events = chunkRelativeMove(100, -50, 40);
    expect(events.every((e) => e.kind === "move")).toBe(true);
    const sumX = events.reduce((a, e) => a + (e.kind === "move" ? e.dx : 0), 0);
    const sumY = events.reduce((a, e) => a + (e.kind === "move" ? e.dy : 0), 0);
    expect(sumX).toBe(100);
    expect(sumY).toBe(-50);
    for (const e of events) {
      if (e.kind === "move") {
        expect(Math.abs(e.dx)).toBeLessThanOrEqual(40);
        expect(Math.abs(e.dy)).toBeLessThanOrEqual(40);
      }
    }
  });
});

describe("PointerCalibration", () => {
  it("starts UNCALIBRATED with portrait defaults", () => {
    const cal = new PointerCalibration();
    expect(cal.state).toBe("UNCALIBRATED");
    expect(cal.screenWidth).toBe(DEFAULT_PORTRAIT_WIDTH);
    expect(cal.screenHeight).toBe(DEFAULT_PORTRAIT_HEIGHT);
  });

  it("swaps dimensions in landscape", () => {
    const cal = new PointerCalibration();
    cal.setOrientation("landscape");
    expect(cal.screenWidth).toBe(DEFAULT_PORTRAIT_HEIGHT);
    expect(cal.screenHeight).toBe(DEFAULT_PORTRAIT_WIDTH);
    expect(cal.state).toBe("INVALID");
  });

  it("calibrate emits home moves then READY at (0,0)", () => {
    const cal = new PointerCalibration();
    const events = cal.beginCalibrate();
    expect(events).toHaveLength(HOME_STEPS);
    expect(events.every((e) => e.kind === "move" && e.dx === HOME_STEP && e.dy === HOME_STEP)).toBe(
      true,
    );
    expect(cal.state).toBe("READY");
    expect(cal.estimatedX).toBe(0);
    expect(cal.estimatedY).toBe(0);
  });

  it("planTap returns [] when not READY", () => {
    const cal = new PointerCalibration();
    expect(cal.planTap(0.5, 0.5)).toEqual([]);
  });

  it("planTap produces chunked moves then click down/up and updates estimate", () => {
    const cal = new PointerCalibration();
    cal.beginCalibrate();
    const events = cal.planTap(0.5, 0.25);
    expect(events.length).toBeGreaterThan(2);
    const lastTwo = events.slice(-2);
    expect(lastTwo[0]).toEqual({ kind: "click", button: "left", pressed: true });
    expect(lastTwo[1]).toEqual({ kind: "click", button: "left", pressed: false });

    const moves = events.filter((e) => e.kind === "move");
    const sumX = moves.reduce((a, e) => a + (e.kind === "move" ? e.dx : 0), 0);
    const sumY = moves.reduce((a, e) => a + (e.kind === "move" ? e.dy : 0), 0);
    expect(sumX).toBe(Math.trunc(0.5 * DEFAULT_PORTRAIT_WIDTH));
    expect(sumY).toBe(Math.trunc(0.25 * DEFAULT_PORTRAIT_HEIGHT));
    expect(cal.estimatedX).toBe(0.5 * DEFAULT_PORTRAIT_WIDTH);
    expect(cal.estimatedY).toBe(0.25 * DEFAULT_PORTRAIT_HEIGHT);
  });

  it("rejects out-of-range normalized coords", () => {
    const cal = new PointerCalibration();
    cal.beginCalibrate();
    expect(cal.planTap(-0.1, 0.5)).toEqual([]);
    expect(cal.planTap(0.5, 1.1)).toEqual([]);
  });

  it("orientation change invalidates READY state", () => {
    const cal = new PointerCalibration();
    cal.beginCalibrate();
    expect(cal.state).toBe("READY");
    cal.setOrientation("landscape");
    expect(cal.state).toBe("INVALID");
    expect(cal.planTap(0.1, 0.1)).toEqual([]);
  });
});
