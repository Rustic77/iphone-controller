import { describe, it, expect } from "vitest";
import { InMemoryDeviceStore } from "../src/stores/deviceStore.js";
import { DevUserStore } from "../src/stores/userStore.js";
import { SessionManager } from "../src/auth.js";

describe("device authentication", () => {
  const store = new InMemoryDeviceStore([
    { id: "devA", name: "A", secret: "correct-horse", ownerId: "userA" },
  ]);

  it("accepts a device with the right id + secret", () => {
    const rec = store.verifyDevice("devA", "correct-horse");
    expect(rec).not.toBeNull();
    expect(rec?.ownerId).toBe("userA");
  });

  it("rejects a wrong secret", () => {
    expect(store.verifyDevice("devA", "wrong")).toBeNull();
  });

  it("rejects an unknown device id", () => {
    expect(store.verifyDevice("ghost", "correct-horse")).toBeNull();
  });

  it("rejects empty credentials", () => {
    expect(store.verifyDevice("", "")).toBeNull();
  });
});

describe("operator authentication", () => {
  const users = new DevUserStore("admin", "hunter2", "dev-operator");

  it("accepts correct credentials", () => {
    expect(users.verifyLogin("admin", "hunter2")?.id).toBe("dev-operator");
  });

  it("rejects wrong password", () => {
    expect(users.verifyLogin("admin", "nope")).toBeNull();
  });

  it("rejects wrong username", () => {
    expect(users.verifyLogin("root", "hunter2")).toBeNull();
  });
});

describe("session tokens", () => {
  it("issues a token that verifies back to the same session/user", () => {
    const sm = new SessionManager("server-secret", 60_000);
    const { token, session } = sm.issue("userA");
    const verified = sm.verify(token);
    expect(verified?.id).toBe(session.id);
    expect(verified?.userId).toBe("userA");
  });

  it("rejects a tampered payload", () => {
    const sm = new SessionManager("server-secret", 60_000);
    const { token } = sm.issue("userA");
    const [, sig] = token.split(".");
    const forgedPayload = Buffer.from(
      JSON.stringify({ sid: "attacker", uid: "userB", exp: Date.now() + 60_000 }),
    ).toString("base64url");
    expect(sm.verify(`${forgedPayload}.${sig}`)).toBeNull();
  });

  it("rejects a token signed with a different secret", () => {
    const a = new SessionManager("secret-a", 60_000);
    const b = new SessionManager("secret-b", 60_000);
    const { token } = a.issue("userA");
    expect(b.verify(token)).toBeNull();
  });

  it("rejects an expired token", () => {
    let now = 1000;
    const sm = new SessionManager("server-secret", 5_000, () => now);
    const { token } = sm.issue("userA");
    now += 6_000;
    expect(sm.verify(token)).toBeNull();
  });

  it("rejects a revoked session", () => {
    const sm = new SessionManager("server-secret", 60_000);
    const { token, session } = sm.issue("userA");
    sm.revoke(session.id);
    expect(sm.verify(token)).toBeNull();
  });

  it("rejects malformed tokens without throwing", () => {
    const sm = new SessionManager("server-secret", 60_000);
    expect(sm.verify(undefined)).toBeNull();
    expect(sm.verify("")).toBeNull();
    expect(sm.verify("garbage")).toBeNull();
    expect(sm.verify("a.b.c")).toBeNull();
  });
});
