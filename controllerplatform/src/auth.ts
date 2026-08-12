import { createHmac, randomBytes, timingSafeEqual } from "node:crypto";

/**
 * Browser session tokens.
 *
 * A token is a signed, opaque bearer credential:  base64url(payload).base64url(hmac)
 * where payload = { sid, uid, exp }. The signature is HMAC-SHA256 over the
 * payload segment using SERVER_SECRET. This is a compact JWT-like token without
 * pulling in a JWT dependency; swap for real JWT/OAuth later if desired.
 *
 * The server also keeps an in-memory session table so sessions can be revoked
 * (e.g. on logout) and so we never rely solely on the token being unexpired.
 */

export interface SessionPayload {
  sid: string;
  uid: string;
  /** epoch ms expiry */
  exp: number;
}

export interface Session {
  id: string;
  userId: string;
  createdAt: number;
  expiresAt: number;
}

function b64url(buf: Buffer): string {
  return buf.toString("base64url");
}

function sign(secret: string, payloadSeg: string): string {
  return b64url(createHmac("sha256", secret).update(payloadSeg).digest());
}

function constantTimeEqual(a: string, b: string): boolean {
  const ab = Buffer.from(a);
  const bb = Buffer.from(b);
  if (ab.length !== bb.length) return false;
  return timingSafeEqual(ab, bb);
}

export class SessionManager {
  private readonly sessions = new Map<string, Session>();

  constructor(
    private readonly secret: string,
    private readonly ttlMs: number,
    private readonly now: () => number = Date.now,
  ) {}

  /** Create a session for a user and return the signed bearer token. */
  issue(userId: string): { token: string; session: Session } {
    const sid = randomBytes(18).toString("base64url");
    const createdAt = this.now();
    const expiresAt = createdAt + this.ttlMs;
    const session: Session = { id: sid, userId, createdAt, expiresAt };
    this.sessions.set(sid, session);

    const payload: SessionPayload = { sid, uid: userId, exp: expiresAt };
    const payloadSeg = b64url(Buffer.from(JSON.stringify(payload)));
    const sigSeg = sign(this.secret, payloadSeg);
    return { token: `${payloadSeg}.${sigSeg}`, session };
  }

  /**
   * Validate a token: signature must match, must not be expired, and the
   * session must still exist in the table (not revoked). Returns the Session or
   * null. Never throws on malformed input.
   */
  verify(token: string | undefined | null): Session | null {
    if (!token) return null;
    const dot = token.indexOf(".");
    if (dot <= 0) return null;
    const payloadSeg = token.slice(0, dot);
    const sigSeg = token.slice(dot + 1);

    const expected = sign(this.secret, payloadSeg);
    if (!constantTimeEqual(sigSeg, expected)) return null;

    let payload: SessionPayload;
    try {
      payload = JSON.parse(Buffer.from(payloadSeg, "base64url").toString("utf8"));
    } catch {
      return null;
    }
    if (typeof payload.sid !== "string" || typeof payload.exp !== "number") return null;

    const session = this.sessions.get(payload.sid);
    if (!session) return null;
    if (this.now() >= session.expiresAt) {
      this.sessions.delete(session.id);
      return null;
    }
    return session;
  }

  revoke(sessionId: string): void {
    this.sessions.delete(sessionId);
  }
}
