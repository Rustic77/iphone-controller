import { timingSafeEqual } from "node:crypto";

export interface User {
  id: string;
  username: string;
}

/**
 * Operator (browser) credential store.
 *
 * The MVP ships a single dev operator configured from environment variables.
 * Swap this implementation for a real account system (DB + hashed passwords)
 * without touching the rest of the server — only this interface is consumed.
 */
export interface UserStore {
  verifyLogin(username: string, password: string): User | null;
  getUser(id: string): User | null;
}

function safeEqual(a: string, b: string): boolean {
  const ab = Buffer.from(a);
  const bb = Buffer.from(b);
  if (ab.length !== bb.length) return false;
  return timingSafeEqual(ab, bb);
}

/** Single-operator store backed by DEV_USERNAME / DEV_PASSWORD. */
export class DevUserStore implements UserStore {
  private readonly user: User;

  constructor(
    private readonly username: string,
    private readonly password: string,
    userId: string,
  ) {
    this.user = { id: userId, username };
  }

  verifyLogin(username: string, password: string): User | null {
    // Compare both fields with constant-time equality to avoid leaking which
    // half was wrong via timing.
    const okUser = safeEqual(username, this.username);
    const okPass = safeEqual(password, this.password);
    return okUser && okPass ? this.user : null;
  }

  getUser(id: string): User | null {
    return id === this.user.id ? this.user : null;
  }
}
