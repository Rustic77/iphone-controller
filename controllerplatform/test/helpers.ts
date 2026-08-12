import { Hub } from "../src/hub.js";
import { InMemoryDeviceStore, type DeviceRecord } from "../src/stores/deviceStore.js";
import { silentLogger } from "../src/logger.js";
import type { Transport } from "../src/types.js";

/** Records everything the Hub sends, for assertions. */
export class FakeTransport implements Transport {
  readonly sent: any[] = [];
  closed = false;

  send(data: string): void {
    this.sent.push(JSON.parse(data));
  }
  close(): void {
    this.closed = true;
  }

  ofType(type: string): any[] {
    return this.sent.filter((m) => m.type === type);
  }
  last(): any {
    return this.sent[this.sent.length - 1];
  }
  clear(): void {
    this.sent.length = 0;
  }
}

/** A clock you can advance by hand. */
export class TestClock {
  constructor(public t = 1_000_000) {}
  now = (): number => this.t;
  advance(ms: number): void {
    this.t += ms;
  }
}

export const DEVICES: DeviceRecord[] = [
  { id: "devA", name: "Device A", secret: "secretA", ownerId: "userA" },
  { id: "devB", name: "Device B", secret: "secretB", ownerId: "userB" },
];

export function makeHub(staleCommandMs = 2000, clock = new TestClock()) {
  const deviceStore = new InMemoryDeviceStore(DEVICES.map((d) => ({ ...d })));
  const hub = new Hub({ deviceStore, logger: silentLogger, staleCommandMs, now: clock.now });
  return { hub, deviceStore, clock };
}

/** Connect a device by id (using the seed record) and return its transport. */
export function connectDevice(hub: Hub, deviceStore: InMemoryDeviceStore, id: string): FakeTransport {
  const record = deviceStore.getDevice(id);
  if (!record) throw new Error("no such device " + id);
  const t = new FakeTransport();
  hub.addDevice(record, t);
  return t;
}
