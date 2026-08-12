/**
 * Per control-session sequence + staleness gate.
 *
 * A "control session" begins when a browser successfully claims a device and
 * ends on release/disconnect. Within it the browser stamps every input with a
 * strictly increasing `seq` and a client timestamp `ts` (epoch ms).
 *
 * Rules:
 *   - Duplicate / out-of-order:  seq <= lastAcceptedSeq  -> drop
 *   - Stale:                     now - ts > staleMs       -> drop
 *
 * Dropping on `seq <= lastAcceptedSeq` collapses both "duplicate" (==) and
 * "reordered/late" (<) into one deterministic rule, which is what you want for
 * real-time input where only the newest state matters.
 */

export type RejectReason = "duplicate" | "stale" | "bad_seq" | "bad_ts";

export type AcceptResult = { ok: true } | { ok: false; reason: RejectReason };

export class SequenceTracker {
  private lastSeq = -Infinity;

  constructor(private readonly staleMs: number) {}

  /** The highest sequence number accepted so far (-Infinity before any). */
  get lastAcceptedSeq(): number {
    return this.lastSeq;
  }

  accept(seq: number, ts: number, now: number): AcceptResult {
    if (!Number.isFinite(seq)) return { ok: false, reason: "bad_seq" };
    if (!Number.isFinite(ts)) return { ok: false, reason: "bad_ts" };

    if (seq <= this.lastSeq) return { ok: false, reason: "duplicate" };

    // Stale: older than the allowed window. Guard against clock skew producing
    // a "future" ts by only rejecting when strictly too old.
    if (now - ts > this.staleMs) return { ok: false, reason: "stale" };

    this.lastSeq = seq;
    return { ok: true };
  }
}
