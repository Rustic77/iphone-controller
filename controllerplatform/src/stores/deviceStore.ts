import { readFileSync } from "node:fs";
import { timingSafeEqual } from "node:crypto";

export interface DeviceRecord {
  id: string;
  name: string;
  /** Long-lived per-device secret presented on connect. */
  secret: string;
  /** Operator (user id) that owns this device. Enforces cross-tenant isolation. */
  ownerId: string;
}

/**
 * Device credential store.
 *
 * Registration for the MVP == presence in the seed JSON file. Replace with a
 * DB-backed provisioning flow later; the rest of the server only depends on
 * this interface.
 */
export interface DeviceCredentialStore {
  /** Returns the record if the (id, secret) pair is valid, else null. */
  verifyDevice(deviceId: string, secret: string): DeviceRecord | null;
  getDevice(deviceId: string): DeviceRecord | null;
  listByOwner(ownerId: string): DeviceRecord[];
}

function safeEqual(a: string, b: string): boolean {
  const ab = Buffer.from(a);
  const bb = Buffer.from(b);
  if (ab.length !== bb.length) return false;
  return timingSafeEqual(ab, bb);
}

export class InMemoryDeviceStore implements DeviceCredentialStore {
  private readonly byId = new Map<string, DeviceRecord>();

  constructor(records: DeviceRecord[]) {
    for (const r of records) {
      if (this.byId.has(r.id)) {
        throw new Error(`Duplicate device id in device store: ${r.id}`);
      }
      this.byId.set(r.id, r);
    }
  }

  verifyDevice(deviceId: string, secret: string): DeviceRecord | null {
    const rec = this.byId.get(deviceId);
    if (!rec) return null;
    return safeEqual(secret, rec.secret) ? rec : null;
  }

  getDevice(deviceId: string): DeviceRecord | null {
    return this.byId.get(deviceId) ?? null;
  }

  listByOwner(ownerId: string): DeviceRecord[] {
    return [...this.byId.values()].filter((d) => d.ownerId === ownerId);
  }
}

/** Load and validate a devices seed file (see devices.example.json). */
export function loadDeviceStoreFromFile(path: string): InMemoryDeviceStore {
  let raw: string;
  try {
    raw = readFileSync(path, "utf8");
  } catch (err) {
    throw new Error(
      `Could not read devices file at "${path}". Copy devices.example.json to devices.json. (${(err as Error).message})`,
    );
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch (err) {
    throw new Error(`devices file "${path}" is not valid JSON: ${(err as Error).message}`);
  }

  const list = (parsed as { devices?: unknown }).devices;
  if (!Array.isArray(list)) {
    throw new Error(`devices file "${path}" must contain a "devices" array`);
  }

  const records: DeviceRecord[] = list.map((d, i) => {
    const rec = d as Partial<DeviceRecord>;
    for (const field of ["id", "name", "secret", "ownerId"] as const) {
      if (typeof rec[field] !== "string" || rec[field] === "") {
        throw new Error(`devices[${i}] is missing string field "${field}"`);
      }
    }
    return rec as DeviceRecord;
  });

  return new InMemoryDeviceStore(records);
}
