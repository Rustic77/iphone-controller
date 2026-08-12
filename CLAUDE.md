# CLAUDE.md — iPhone Controller (ESP32-S3 USB HID)

Project guidance for Claude Code working in this repository.

## Project goal

Build a remote iPhone controller. The ESP32-S3 presents itself to an iPhone as a
standard USB Human Interface Device (HID) — a mouse and keyboard — so it can
drive the phone over the wire. This repository is the **low-level USB HID
foundation** only.

**Current phase:** Outbound cloud control relay. On top of the local LAN
controller, the ESP32 now also dials **out** to a configured cloud relay server
over a secure WebSocket (WSS/TLS) and accepts the relay's control protocol,
translating it into the same `input_actions`. It never opens an inbound
Internet-facing port — only the outbound socket carries cloud control, so no
port-forwarding is needed. Cloud credentials live in NVS (never hardcoded). See
"Cloud control (outbound relay client)". LAN control is unchanged and keeps
working.

**Previous phase:** Wi-Fi provisioning + Station mode on top of the local web
controller. On first boot (no saved Wi-Fi) the ESP32 comes up as a provisioning
SoftAP; the user enters their router SSID/password in the web UI; it's saved to
NVS and the device reboots into Station mode and joins the LAN. The same web
UI + JSON API + WebSocket control the iPhone in either mode (see "Local Wi-Fi
control"). Under that sits the reusable HID controller: all HID output is routed
through a command queue drained by a single worker task (see "HID controller").

The BOOT button is a built-in test / reset:

- **Short press (<600 ms):** move mouse 50 units right, then one left click.
- **Long press (600 ms–5 s):** type exactly `Hello from ESP32`.
- **Very long hold (≥5 s):** factory-reset Wi-Fi config (erase NVS creds) and
  reboot into provisioning.

Nothing moves or types automatically on boot. Verified working on Windows and
iPhone. USB HID works regardless of Wi-Fi state.

## Hardware

| Item              | Value                                                       |
| ----------------- | ----------------------------------------------------------- |
| Board             | DORHEA dual-USB-C ESP32-S3 development board                |
| Module            | ESP32-S3-WROOM                                              |
| Variant           | N16R8                                                       |
| Flash             | 16 MB                                                       |
| PSRAM             | 8 MB (octal)                                                |
| BOOT button       | GPIO0 (active-low), used to trigger the HID action          |

### N16R8 configuration

- 16 MB flash, 8 MB octal PSRAM.
- Flash size and PSRAM must match the module: build for `esp32s3`, flash size
  16 MB, octal SPIRAM. If PSRAM/flash options are added later they belong in
  `sdkconfig.defaults`; the current minimal proof does not require PSRAM.

## The two USB-C ports

This board has **two physical USB-C ports** with different roles. Do not confuse
them.

| Port label | Connects to        | Purpose                                                       |
| ---------- | ------------------ | ------------------------------------------------------------- |
| **COM**    | Windows dev PC     | Flashing firmware **and** UART/debug logging (serial monitor) |
| **USB**    | iPhone (via hub)   | Native ESP32-S3 USB-OTG; enumerates as the USB HID device     |

- **COM port** = the built-in USB-to-UART bridge. Used with `idf.py flash` and
  `idf.py monitor`. All `ESP_LOGx` output appears here.
- **USB port** = the ESP32-S3's native USB peripheral (D+/D-). This is what the
  host (PC now, iPhone later) sees as a HID mouse/keyboard. Never expect log
  output on this port.

## Software / toolchain

- **Framework:** ESP-IDF (v6.0.2 vendored at `../v6.0.2/esp-idf`).
- **Target:** `esp32s3`.
- **Language:** C / C++ (do **not** convert this project to Arduino).
- **USB stack:** TinyUSB via the `espressif/esp_tinyusb` managed component
  (`^2.0.1~1`), which wraps upstream TinyUSB.

## TinyUSB architecture

- This project started from Espressif's official **`tusb_hid`** example and
  preserves its structure.
- `tinyusb_driver_install()` starts a **dedicated FreeRTOS task** that calls
  `tud_task()` in a loop (see `managed_components/espressif__esp_tinyusb/
  tinyusb_task.c`). `app_main()` runs in a **separate** task.
- Because of that separation, `vTaskDelay()` inside `app_main()` (or helpers it
  calls) yields to the scheduler and **does not block** USB processing. This is
  why the click sequence can wait ~100 ms / ~50 ms between reports safely.
- The device is a **composite HID interface** (single interface, instance 0)
  exposing both a keyboard and a mouse. The report descriptor
  (`hid_report_descriptor` in `main/tusb_hid_example_main.c`) declares both, with
  report IDs `HID_ITF_PROTOCOL_KEYBOARD` (1) and `HID_ITF_PROTOCOL_MOUSE` (2).
- Report APIs used: `tud_hid_mouse_report(report_id, buttons, x, y, v, h)` and
  (available but currently unused) `tud_hid_keyboard_report()`.
- USB lifecycle callbacks are implemented and must stay working:
  `tud_mount_cb` / `tud_umount_cb` (mount/unmount logging) and
  `tud_suspend_cb` / `tud_resume_cb` (+ remote-wakeup handling).

## HID controller (`main/hid_controller.c` / `.h`)

A reusable layer that turns the raw HID proof into an API. The strict rule:
**exactly one FreeRTOS task ever transmits HID reports through TinyUSB.**

```
other firmware modules  (any task)
        |  hid_mouse_* / hid_keyboard_* / hid_release_all()
        v
FreeRTOS command queue  (fixed-size items, FIFO)
        |
        v
single HID worker task  (the ONLY transmitter)
        |  tud_hid_mouse_report() / tud_hid_keyboard_report()
        v
TinyUSB  -->  iPhone / PC
```

Key rules baked into the controller — do not regress these:

- **Single transmitter.** Public API functions only *enqueue*. Only the worker
  task calls `tud_hid_*_report`. Never call TinyUSB HID transmit from another
  task.
- **State ownership.** The current mouse-button / keyboard-modifier / keycode
  state is owned exclusively by the worker task (no locks). The only cross-task
  flag is `s_usb_connected` (single writer: the USB event callback).
- **Movement splitting.** `hid_mouse_move()` takes `int16_t`; the worker splits
  motion larger than a report's `int8_t` range into multiple reports. Values are
  never silently truncated.
- **Click safety.** A click is always DOWN → configurable delay → UP.
- **Disconnect/reconnect.** On USB unmount the worker clears all tracked state
  and flushes the queue; on remount it starts fully released.
- **Endpoint-not-ready.** `hid_wait_ready()` retries (bounded) rather than
  crashing or reordering.
- **Queue-full policy.** Critical commands (button/key DOWN/UP, RELEASE_ALL,
  typed text) block briefly and return an explicit error if they still cannot be
  enqueued — never silently dropped. Movement/scroll may be dropped, but always
  with a log.

`hid_controller_init()` is called from `app_main()` after
`tinyusb_driver_install()`. `usb_event_cb` in the main file forwards
attach/detach to `hid_controller_usb_set_connected()`.

## Input actions (`main/input_actions.c` / `.h`)

A higher-level gesture layer built **entirely on top of `hid_controller.h`** — it
never calls TinyUSB directly. It turns raw HID primitives into human-style
actions: `input_click`, `input_double_click`, `input_long_press`,
`input_drag_relative`, `input_move_relative`, `input_scroll`, `input_type_text`,
named-key taps (`input_press_enter/backspace/tab/escape`), and the emergency
`input_release_all`.

Rules baked in — do not regress:

- **Layering.** `input_actions` → `hid_controller` → TinyUSB. Never shortcut.
- **Drag** = move (if any) as `DOWN → interpolated small moves over duration →
  UP`. Motion is spread across many small reports (never one giant report),
  using absolute-target interpolation so the final delta is exact.
- **Long press** = `DOWN → hold → UP`. **Double click** = `click → gap → click`.
- **Safety bounds.** Movement is clamped to `INPUT_MOVE_MAX` and durations to
  `INPUT_DURATION_MAX_MS` (clamping is logged, never silent).
- **No stuck buttons/keys.** Every gesture that presses guarantees a matching
  release even if an intermediate step fails; key taps always attempt key-up.
- **Threading.** Runs in the caller's task and may block (vTaskDelay) for timed
  gestures. Intended for a single input-producer context; concurrent callers
  must coordinate externally.

The BOOT test in the main file now drives this layer: short press →
`input_move_relative` + `input_click`; long press → `input_type_text`.

## Local Wi-Fi control (`main/wifi_ap.*`, `main/control_server.*`)

Local-only remote control. **No cloud, no internet dependency.** Two Wi-Fi roles
managed by `wifi_ap.c` (it's a small Wi-Fi manager despite the name):

```
laptop/phone browser --Wi-Fi--> ESP32 (SoftAP or LAN) --> HTTP/WS server
      --> input_actions / hid_controller --> TinyUSB --> iPhone
```

### Provisioning ↔ Station state machine

- **No saved creds → provisioning SoftAP.** SSID `iPhoneController-XXXX` (`XXXX`
  = last 2 SoftAP MAC bytes), WPA2, IP `192.168.4.1`. The web UI's Wi-Fi form
  takes the router SSID/password.
- **Save → NVS → reboot → Station mode.** Joins the saved router, gets a LAN IP;
  the control UI is reachable at that IP. Auto-reconnects on drop.
- **Repeated STA failures (≥`WIFI_STA_MAX_RETRY`, 15) → provisioning AP returns**
  (APSTA) so the user can re-provision, while STA keeps retrying in the
  background.
- **Credentials in NVS** (namespace `wifi`, keys `ssid`/`pass`). The **user's
  router password is never hardcoded** and never returned by any endpoint. Only
  the ESP32's *own* provisioning-AP password is a constant (`WIFI_AP_PASSWORD` in
  `wifi_ap.h`) — that's not the user's Wi-Fi.
- **Reset paths:** `POST /api/wifi/reset` (web "RESET WIFI" button) or a **≥5 s
  BOOT hold** erase NVS creds and reboot into provisioning. `POST /api/wifi/save`
  provisions + reboots.
- **On STA disconnect after being connected → `input_release_all()`** (req 9),
  same safety as a WS/close drop.

- **HTTP server** (`control_server.c`, `esp_http_server`, port 80, runs in every
  mode):
  - `GET /` — embedded trackpad UI (HTML/CSS/JS inlined in the C source; no
    React/Node/build step). Pointer Events drive `/api/move` (throttled/coalesced
    ~20 Hz) and taps drive `/api/click`.
  - `GET /api/status` — `{usb_mounted, hid_ready, uptime_ms, wifi_clients,
    wifi_mode, wifi_connected, ssid, ip_address, rssi}` (rssi is `null` when
    unavailable; **no passwords are ever returned**).
  - `POST /api/move|click|mousedown|mouseup|scroll|text|release-all`.
  - `POST /api/wifi/save` `{ssid, password}` → saves to NVS + reboots.
  - `POST /api/wifi/reset` → erases NVS creds + reboots into provisioning.
- **JSON**: ESP-IDF 6.0 no longer bundles cJSON, so a small strict extractor in
  `control_server.c` parses the fixed schemas (validates presence + type, bounds
  every number, caps text length). Do not reintroduce a cJSON dependency without
  confirming it's available offline.
- **HTTP handlers never call TinyUSB.** They call `input_actions` or enqueue via
  `hid_controller`.
- **Abuse resistance:** token-bucket rate limiting (429 when exceeded); HID queue
  already drops non-critical commands when full, so flooding cannot crash.
- **Failsafe:** a watchdog task calls `input_release_all()` if a client holds a
  button (via `/api/mousedown`) and then goes silent past a 2 s timeout.

Startup order in `app_main`: `tinyusb_driver_install` → `hid_controller_init` →
`wifi_start` → `control_server_start` → `cloud_client_start`.

### Real-time channel: WebSocket (`/ws`)

Real-time pointer control uses a WebSocket; REST stays for `GET /api/status` and
`POST /api/text` (and the REST mouse endpoints remain for curl debugging).
Requires `CONFIG_HTTPD_WS_SUPPORT=y` (in `sdkconfig.defaults`).

Compact JSON protocol (client → server), one object per frame:
```
{"session":"ab12cd34","seq":1001,"type":"move","dx":12,"dy":-4}
```
`type` ∈ `move | down | up | click | scroll | release | ping`. Server → client:
`{"type":"pong"}` for a ping (that's the only server-originated frame).

Session/sequencing rules (all enforced in `control_server.c`):
- **New session per connection.** The client generates a random session id in
  `ws.onopen` and includes it in every message. On the WS handshake the server
  binds the controlling **socket fd**, cancels any pending HID commands
  (`hid_controller_cancel_pending`) and releases all; it adopts the client's
  session id on the first message. The socket fd — not the string — is the real
  authority, which avoids any handshake/first-frame race. (An earlier
  server-assigned "welcome" frame was removed because queuing it during the
  handshake could run before the 101 was flushed, leaving the client with no
  session so it sent nothing.)
- **Monotonic seq**, duplicates/regressions ignored. DOWN/UP always carry a
  higher seq over ordered TCP, so they are never seq-dropped, and they enqueue as
  *critical* HID commands (never silently lost).
- **Stale connections rejected** — only messages on the active socket fd act, so
  a reconnect cleanly supersedes the old one and old queued commands never run.
- **Move coalescing** — deltas accumulate and merge when the HID queue is full
  (`ws_do_move`), so flooding degrades gracefully instead of crashing.
- **Safety releases:** WS socket close → `input_release_all()` (via `close_fn`);
  heartbeat gap > 3 s → release + force-close so the browser reconnects; browser
  `blur`/`visibilitychange`/`pointercancel` send `release`.

The embedded UI drives all of this from Pointer Events (tap → click, drag → move,
long-press → down/move/up drag) and auto-reconnects on close.

**Robustness in the UI:** every pointer action goes through `act(type,data)`,
which uses the WebSocket **only after it is confirmed bidirectional** and
otherwise **falls back to the equivalent REST endpoint**. Confirmation works via
`wsAlive`: the client pings once/second and the server replies (`pong`, plus an
`ack` for each discrete action); the first reply flips `wsAlive` true and the
status `Link:` from `ws?` → `ws`. Until then the trackpad runs on REST, so it
always works even if a browser/network can't carry a WebSocket. The status line
also shows `tx`/`rx` counters (frames sent / server replies received) — handy
for diagnosing the link at a glance.

**Caching footgun (important):** the root page is served `Cache-Control:
no-store`. Browsers aggressively cache the embedded UI, and after a firmware
update a stale cached page will talk to old/removed endpoints and *look* broken
(this cost us a long debugging detour — the WebSocket appeared dead purely
because the browser was running an old cached `index`). When testing UI changes,
hard-refresh or load `http://192.168.4.1/?v=N` / use a private window. The
version tag in the status line (`vN`) exists to confirm you're on fresh JS.

Per-frame WS logging (`ws frame`, `ws msg`) is at `ESP_LOGD` so high-frequency
moves don't spam the UART; discrete actions (`ws click/down/up`) log at info.

Note: enabling Wi-Fi grew the app image (~0.84 MB). Adding the outbound TLS
cloud client (mbedTLS + CA bundle) pushes it past 1 MB, so the build now uses the
**large single-app** partition layout (`CONFIG_PARTITION_TABLE_SINGLE_APP_LARGE`,
~1.5 MB app) — still within the 2 MB flash config. See `sdkconfig.defaults`.

## Cloud control (outbound relay client) (`main/cloud_client.*`)

Remote control from anywhere, via a cloud relay, **without exposing the ESP32**.
The ESP32 dials **out** to a configured relay server over a secure WebSocket
(WSS/TLS) and receives the relay's device-facing control protocol, translating it
into the same `input_actions`. It never opens an inbound Internet-facing port, so
no port-forwarding / NAT hole is needed.

```
cloud relay  --WSS/TLS-->  cloud_client (validate: session/seq/stale)
   --queue-->  cloud worker task  -->  input_actions / hid_controller
   -->  TinyUSB  -->  iPhone
```

The relay server lives in a **separate project** (`../controllerplatform`, a
TypeScript/Node app); its `docs/PROTOCOL.md` is the authoritative wire spec.

### Security & provisioning

- **TLS certificate verification** for `wss://` via the bundled Mozilla root CAs
  (`esp_crt_bundle_attach`; `CONFIG_MBEDTLS_CERTIFICATE_BUNDLE`). `ws://` (no
  TLS) is allowed for LAN development against a laptop-hosted relay.
- **Credentials never hardcoded** (req). `device_id` + a unique per-device
  `secret` are provisioned into **NVS** (namespace `cloud`, keys `uri`,
  `dev_id`, `secret`) and loaded at boot. The secret is presented in the WS
  handshake headers (`x-device-id` / `x-device-secret`) and is **never logged**
  or returned by `/api/status`.
- Provision locally (LAN/AP only), then reboot to apply:
  - `POST /api/cloud/config` `{uri, device_id, secret}` → saves to NVS + reboots.
  - `POST /api/cloud/reset` → erases cloud NVS + reboots.

### Behavior (mirrors the LAN WS discipline)

- **Authenticate on connect** — auth is the handshake headers; a successful
  connect == authenticated. The server's `hello` confirms it.
- **Heartbeat** — ws-level ping/pong (`ping_interval_sec`, `pingpong_timeout_sec`);
  any received frame refreshes `last_cloud_message_ms`.
- **Reconnect** — auto-reconnect is disabled; a supervisor task reconnects with
  **exponential backoff** (1 s → 30 s, +jitter), reset on a healthy connect.
- **Session / sequence** — the relay stamps each `input` with a control-`session`
  and a monotonic `seq`. The client latches the first session, **rejects** other
  sessions, and **drops** `seq <= last` (duplicate / stale). A server `release_all`
  precedes every session change and resets this state, so a **reconnect never
  replays** old input (a fresh connection starts with no latched session).
- **Fail-safe** — on any disconnect/close/error the client calls
  `input_release_all()` (drop all held buttons/keys), matching the LAN path.
- **Non-blocking WS task** — the WS event task only validates + enqueues; a
  dedicated worker task drains the queue and calls `input_actions` (which may
  block for typing/taps), so a gesture never stalls frame reads or heartbeats.

### Status fields (added to `GET /api/status`)

`cloud_provisioned`, `cloud_connected`, `cloud_session`, `cloud_host`,
`cloud_device_id`, `last_cloud_message_ms`. Never any secret.

## iPhone HID objective

The end goal is for the **USB port** to enumerate on an iPhone 17 (through a
powered USB-C hub) as a standard HID mouse + keyboard, letting the ESP32 remotely
control the phone. iOS accepts standard USB HID input devices; this phase proves
the HID plumbing works against a Windows PC first. No iOS-specific USB behavior is
assumed or invented.

## Guardrails

- **Do not rewrite working low-level USB code merely for style.** The
  descriptors, `tinyusb_driver_install` flow, HID callbacks, and mount/suspend
  handling are known-good; change them only when a task genuinely requires it.
- **Do not add** (this phase): Bluetooth, an iOS app, screen/video streaming,
  databases, or OTA. (Wi-Fi, the local HTTP/WebSocket server, and the outbound
  cloud relay client are now part of the firmware — see their sections.)
- **Cloud is outbound-only.** The ESP32 must never open an inbound
  Internet-facing listener. Cloud control rides the single outbound WSS socket
  that `cloud_client` dials. The LAN HTTP server stays LAN-only.
- **Never hardcode or log the device secret.** It lives in NVS and in the one
  in-RAM header buffer used to authenticate; it is never printed or returned by
  any endpoint.
- **Do not block the TinyUSB task** with long busy-waits; use `vTaskDelay()` in
  the app task as above.
- **Do not invent undocumented USB functionality.** Stick to supported
  TinyUSB / ESP-IDF APIs.
- Keep it minimal — no speculative abstractions yet.

## Files that matter

- `main/tusb_hid_example_main.c` — USB descriptors, TinyUSB HID callbacks,
  USB event callback, and the BOOT-button test loop. Does **not** transmit HID
  reports directly — it calls the HID controller API.
- `main/hid_controller.c` / `main/hid_controller.h` — the reusable HID
  controller: command queue, single worker task, all `tud_hid_*_report` calls.
- `main/input_actions.c` / `main/input_actions.h` — high-level gesture layer on
  top of the HID controller (click, drag, long press, typing, named keys).
- `main/wifi_ap.c` / `main/wifi_ap.h` — Wi-Fi manager: provisioning SoftAP ↔
  Station state machine, NVS credentials, status snapshot, factory reset. The
  **provisioning-AP** password (not the user's Wi-Fi) is the one constant here
  (`WIFI_AP_PASSWORD`); the router password is provisioned at runtime into NVS.
- `main/control_server.c` / `main/control_server.h` — HTTP server: embedded UI,
  REST API (status/text + debug mouse endpoints), WebSocket `/ws` real-time
  control, JSON validation, rate limiting, session/seq handling, failsafes. Also
  hosts the LAN-only cloud provisioning endpoints (`/api/cloud/config|reset`) and
  the cloud status fields in `/api/status`.
- `main/cloud_client.c` / `main/cloud_client.h` — outbound WSS cloud relay
  client: NVS provisioning, TLS cert verification, header auth, heartbeat,
  exponential-backoff reconnect, session/seq/stale validation, translation to
  `input_actions`, and release-all on disconnect.
- `main/CMakeLists.txt` — registers all sources; `PRIV_REQUIRES` now includes
  `esp_wifi esp_event esp_netif nvs_flash esp_http_server esp_timer` plus
  `esp_websocket_client esp-tls mbedtls` for the cloud client.
- `main/idf_component.yml` — declares `espressif/esp_tinyusb` and
  `espressif/esp_websocket_client`.
- `sdkconfig.defaults` — `CONFIG_TINYUSB_HID_COUNT=1`, WS support, the large
  single-app partition, and the mbedTLS CA certificate bundle.

## Build / flash / monitor

```bash
idf.py set-target esp32s3      # once
idf.py build
idf.py -p <COM_PORT> flash monitor   # e.g. -p COM3 ; the COM-labeled port
```

Exit the monitor with `Ctrl-]`.
