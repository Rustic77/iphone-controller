import type { InputEvent } from "./types.js";

export type CalibrationState = "UNCALIBRATED" | "CALIBRATING" | "READY" | "INVALID";

export type ScreenOrientation = "portrait" | "landscape";

/** iPhone 17-class logical points in portrait (MVP defaults). */
export const DEFAULT_PORTRAIT_WIDTH = 1179;
export const DEFAULT_PORTRAIT_HEIGHT = 2556;

/** Max absolute delta per relative HID move chunk. */
export const MAX_MOVE_STEP = 40;

/** Homing: repeat diagonal moves so the cursor hits a corner from anywhere. */
export const HOME_STEPS = 100;
export const HOME_STEP = -40;

export interface PointerCalibrationOptions {
  portraitWidth?: number;
  portraitHeight?: number;
  maxMoveStep?: number;
  homeSteps?: number;
  homeStep?: number;
}

/**
 * Relative-pointer calibration for video taps.
 *
 * HID mice are relative-only. We home to an estimated (0,0) corner, then plan
 * chunked relative moves to a normalized (0..1) tap target and click.
 */
export class PointerCalibration {
  private readonly portraitWidth: number;
  private readonly portraitHeight: number;
  private readonly maxMoveStep: number;
  private readonly homeSteps: number;
  private readonly homeStep: number;

  state: CalibrationState = "UNCALIBRATED";
  estimatedX = 0;
  estimatedY = 0;
  orientation: ScreenOrientation = "portrait";

  constructor(opts: PointerCalibrationOptions = {}) {
    this.portraitWidth = opts.portraitWidth ?? DEFAULT_PORTRAIT_WIDTH;
    this.portraitHeight = opts.portraitHeight ?? DEFAULT_PORTRAIT_HEIGHT;
    this.maxMoveStep = opts.maxMoveStep ?? MAX_MOVE_STEP;
    this.homeSteps = opts.homeSteps ?? HOME_STEPS;
    this.homeStep = opts.homeStep ?? HOME_STEP;
  }

  get screenWidth(): number {
    return this.orientation === "landscape" ? this.portraitHeight : this.portraitWidth;
  }

  get screenHeight(): number {
    return this.orientation === "landscape" ? this.portraitWidth : this.portraitHeight;
  }

  /**
   * Home the pointer with many move(-40,-40), then mark READY at (0,0).
   * Emits CALIBRATING then READY via the returned events; caller should
   * surface `state` after applying.
   */
  beginCalibrate(): InputEvent[] {
    this.state = "CALIBRATING";
    const events: InputEvent[] = [];
    for (let i = 0; i < this.homeSteps; i++) {
      events.push({ kind: "move", dx: this.homeStep, dy: this.homeStep });
    }
    this.estimatedX = 0;
    this.estimatedY = 0;
    this.state = "READY";
    return events;
  }

  /**
   * Plan relative moves from the estimated position to a normalized tap,
   * then left-click down/up. Returns [] and leaves state unchanged if not READY
   * or coords are out of range.
   */
  planTap(x: number, y: number): InputEvent[] {
    if (this.state !== "READY") return [];
    if (!Number.isFinite(x) || !Number.isFinite(y)) return [];
    if (x < 0 || x > 1 || y < 0 || y > 1) return [];

    const targetX = x * this.screenWidth;
    const targetY = y * this.screenHeight;
    const dx = targetX - this.estimatedX;
    const dy = targetY - this.estimatedY;

    const events = chunkRelativeMove(dx, dy, this.maxMoveStep);
    events.push({ kind: "click", button: "left", pressed: true });
    events.push({ kind: "click", button: "left", pressed: false });

    this.estimatedX = targetX;
    this.estimatedY = targetY;
    return events;
  }

  /**
   * Apply orientation from agent video_metadata. Changing orientation
   * invalidates the estimated cursor position.
   */
  setOrientation(orientation: string | undefined): void {
    if (!orientation) return;
    const next = normalizeOrientation(orientation);
    if (!next) return;
    if (next !== this.orientation) {
      this.orientation = next;
      this.invalidate("orientation_changed");
    } else {
      this.orientation = next;
    }
  }

  /** Force INVALID (e.g. source lost). */
  invalidate(_reason?: string): void {
    this.state = "INVALID";
    this.estimatedX = 0;
    this.estimatedY = 0;
  }

  reset(): void {
    this.state = "UNCALIBRATED";
    this.estimatedX = 0;
    this.estimatedY = 0;
    this.orientation = "portrait";
  }
}

/** Split a large relative move into HID-safe chunks. */
export function chunkRelativeMove(dx: number, dy: number, maxStep = MAX_MOVE_STEP): InputEvent[] {
  const events: InputEvent[] = [];
  let remainX = dx;
  let remainY = dy;
  const step = Math.max(1, Math.abs(maxStep));

  while (remainX !== 0 || remainY !== 0) {
    const mx = clamp(remainX, -step, step);
    const my = clamp(remainY, -step, step);
    // Avoid zero-zero when float residue is tiny.
    if (mx === 0 && my === 0) break;
    events.push({ kind: "move", dx: mx, dy: my });
    remainX -= mx;
    remainY -= my;
  }
  return events;
}

export function normalizeOrientation(raw: string): ScreenOrientation | null {
  const s = raw.trim().toLowerCase();
  if (s === "portrait" || s === "portraitup" || s === "portraitupsidedown") return "portrait";
  if (s === "landscape" || s === "landscapeleft" || s === "landscaperight") return "landscape";
  return null;
}

function clamp(n: number, min: number, max: number): number {
  if (n < min) return min;
  if (n > max) return max;
  // Prefer integers for HID relative moves.
  return Math.trunc(n);
}
