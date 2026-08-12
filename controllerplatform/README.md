# ESP32 iPhone Controller — Cloud Control Relay (MVP)

Secure remote command relay for an **ESP32-S3-based remote iPhone controller**.
The ESP32 drives an iPhone over **USB HID (mouse + keyboard)**; this server lets
an authenticated operator control that ESP32 from a browser, anywhere, without
exposing the ESP32 to the public Internet.

```
Browser ──HTTPS/WSS──▶ Cloud control server ◀──outbound WSS/TLS── ESP32 ──USB HID──▶ iPhone
```

**The ESP32 always dials out.** The server never connects to the device, so no
port forwarding and no public exposure of the ESP32 is required.

> Scope: this is the control-plane MVP. Video streaming is intentionally **not**
> included. See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) and
> [`docs/PROTOCOL.md`](docs/PROTOCOL.md) for the full design and wire protocol.

## Features

- Device registration + per-device credentials (unique secret).
- Online/offline device state with last-seen tracking.
- Outbound WebSocket connections from ESP32 devices, authenticated on connect.
- Authenticated operator (browser) sessions with signed session tokens.
- Claim/control model: one operator controls one device at a time.
- **Tenant isolation** — an operator can only see/claim/control devices they own;
  one user's input can never reach another user's device.
- Per-control-session **sequence IDs**, duplicate + stale command dropping.
- On browser disconnect → server tells the ESP32 to **release all HID state**.
- On device disconnect → the controlling browser sees **offline immediately**.
- ws-level **heartbeat / timeout** handling.
- Structured JSON logs (pino via Fastify).
- Minimal web UI: device list + controller page (trackpad, click, drag, scroll,
  keyboard text entry, emergency RELEASE ALL).

## Tech stack

- **TypeScript** + **Node.js** (>= 18.17)
- **Fastify** — HTTP, static hosting, structured logging
- **ws** — WebSocket server (attached to Fastify's HTTP server)
- **Vitest** — unit tests
- No build step required for dev — runs via `tsx`.

## Project layout

```
controllerplatform/
├─ src/
│  ├─ index.ts             # entrypoint: load config, wire stores, listen
│  ├─ config.ts            # env parsing/validation
│  ├─ server.ts            # Fastify + ws wiring, auth on upgrade, heartbeat
│  ├─ hub.ts               # the relay core (transport-agnostic, unit-tested)
│  ├─ auth.ts              # signed browser session tokens
│  ├─ sequenceTracker.ts   # per-session dedupe + stale gate
│  ├─ logger.ts            # logger interface + silent test logger
│  ├─ types.ts             # shared wire types (the protocol contract)
│  └─ stores/
│     ├─ userStore.ts      # operator credentials (dev = env-backed)
│     └─ deviceStore.ts    # device credentials (dev = JSON seed file)
├─ public/                 # web UI (index = device list, controller page)
├─ test/                   # vitest unit tests
├─ docs/                   # architecture + protocol docs
├─ .env.example            # copy to .env (never commit .env)
└─ devices.example.json    # copy to devices.json (never commit real secrets)
```

## Running locally

**1. Install dependencies**

```bash
npm install
```

**2. Create your local secrets (never committed)**

```bash
# copy the templates
cp .env.example .env
cp devices.example.json devices.json
```

Then edit them:

- In `.env`, set a real `SERVER_SECRET` (the server refuses to start with the
  placeholder) and a `DEV_PASSWORD`. Generate a secret with:

  ```bash
  node -e "console.log(require('crypto').randomBytes(32).toString('hex'))"
  ```

- In `devices.json`, set a unique `secret` for each device. `ownerId` must match
  `DEV_USER_ID` from `.env` (default `dev-operator`) for the dev operator to see
  the device.

**3. Start the server**

```bash
npm run dev     # watch mode (auto-restart on change)
# or
npm start       # run once
```

Open **http://localhost:8080/**, sign in with `DEV_USERNAME` / `DEV_PASSWORD`.

**4. Point a device at it (for testing without hardware)**

A device connects to `ws://localhost:8080/ws/device` and authenticates with
headers `x-device-id` + `x-device-secret`. Example with `websocat`:

```bash
websocat -H 'x-device-id: esp32-lab-01' -H 'x-device-secret: <your-secret>' \
  ws://localhost:8080/ws/device
```

The device will then appear **online** in the UI and can be controlled.

## Commands

| Command             | What it does                                   |
| ------------------- | ---------------------------------------------- |
| `npm run dev`       | Start server in watch mode                     |
| `npm start`         | Start server once                              |
| `npm test`          | Run the unit test suite (vitest)               |
| `npm run test:watch`| Run tests in watch mode                        |
| `npm run typecheck` | Type-check with `tsc --noEmit`                 |

## Tests

Unit tests cover the required behaviors:

- **device authentication** — `test/auth.test.ts`
- **session isolation** — `test/sessionIsolation.test.ts`
- **sequence handling** — `test/sequence.test.ts`
- **stale command rejection** — `test/stale.test.ts`
- **disconnect / release behavior** — `test/disconnect.test.ts`

```bash
npm test
```

## Security notes

- WebSocket control is **never** unauthenticated. Browsers authenticate with a
  signed session token (issued by `/api/login`); devices authenticate with a
  per-device secret on the upgrade request. Unauthenticated upgrades are
  rejected with `401` before any relay logic runs.
- The credential model is intentionally small and swappable — replace
  `DevUserStore` / `InMemoryDeviceStore` with a real account + provisioning
  system without touching the relay core. See
  [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md#credential-model).
- Secrets come from environment variables (`SERVER_SECRET`) and the un-committed
  `devices.json`. `.env`, `.env.*` (except `.env.example`), and `devices.json`
  are git-ignored. **Never commit secrets.**
- Terminate TLS in front of this server (a reverse proxy such as Caddy/nginx, or
  your cloud load balancer) so browser↔server and device↔server links are
  HTTPS/WSS in production.

## Production build (optional)

The MVP runs directly with `tsx`. If you prefer emitted JS, add a `tsc` build
step (flip `noEmit` off with an `outDir`) — the code is standard ESM.
