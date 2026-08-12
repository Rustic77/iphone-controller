| Supported Targets | ESP32-H4 | ESP32-P4 | ESP32-S2 | ESP32-S3 |
| ----------------- | -------- | -------- | -------- | -------- |

# TinyUSB Human Interface Device Example

(See the README.md file in the upper level 'examples' directory for more information about examples.)

Human interface devices (HID) are one of the most common USB devices, it is implemented in various devices such as keyboards, mice, game controllers, sensors and alphanumeric display devices.
In this example, we implement USB keyboard and mouse.
Upon connection to USB host (PC), the example application will sent 'key a/A pressed & released' events and move mouse in a square trajectory. To send these HID reports again, press the BOOT button, that is present on most ESP development boards (GPIO0).

As a USB stack, a TinyUSB component is used.

## How to use example

### Hardware Required

Any ESP board that have USB-OTG supported.

#### Pin Assignment

_Note:_ In case your board doesn't have micro-USB connector connected to USB-OTG peripheral, you may have to DIY a cable and connect **D+** and **D-** to the pins listed below.

See common pin assignments for USB Device examples from [upper level](../../README.md#common-pin-assignments).

Boot signal (GPIO0) is used to send HID reports to USB host.

### Build and Flash

Build the project and flash it to the board, then run monitor tool to view serial output:

```bash
idf.py -p PORT flash monitor
```

(Replace PORT with the name of the serial port to use.)

(To exit the serial monitor, type ``Ctrl-]``.)

See the Getting Started Guide for full steps to configure and use ESP-IDF to build projects.

## Example Output

After the flashing you should see the output at idf monitor:

```
I (290) cpu_start: Starting scheduler on PRO CPU.
I (0) cpu_start: Starting scheduler on APP CPU.
I (310) example: USB initialization
I (310) tusb_desc:
┌─────────────────────────────────┐
│  USB Device Descriptor Summary  │
├───────────────────┬─────────────┤
│bDeviceClass       │ 0           │
├───────────────────┼─────────────┤
│bDeviceSubClass    │ 0           │
├───────────────────┼─────────────┤
│bDeviceProtocol    │ 0           │
├───────────────────┼─────────────┤
│bMaxPacketSize0    │ 64          │
├───────────────────┼─────────────┤
│idVendor           │ 0x303a      │
├───────────────────┼─────────────┤
│idProduct          │ 0x4004      │
├───────────────────┼─────────────┤
│bcdDevice          │ 0x100       │
├───────────────────┼─────────────┤
│iManufacturer      │ 0x1         │
├───────────────────┼─────────────┤
│iProduct           │ 0x2         │
├───────────────────┼─────────────┤
│iSerialNumber      │ 0x3         │
├───────────────────┼─────────────┤
│bNumConfigurations │ 0x1         │
└───────────────────┴─────────────┘
I (480) TinyUSB: TinyUSB Driver installed
I (480) example: USB initialization DONE
I (2490) example: Sending Keyboard report
I (3040) example: Sending Mouse report
```

---

## Cloud relay: testing locally before deploying to the Internet

The firmware can be driven from a cloud **relay server** (separate project at
`../controllerplatform`) over an outbound secure WebSocket. You do **not** need a
real Internet server to test the whole chain — run the relay on your laptop and
have the ESP32 connect to it over your LAN with plain `ws://`. Only once that
works end-to-end do you deploy the relay behind TLS and switch the device to
`wss://`.

See `main/cloud_client.c` and `../controllerplatform/docs/PROTOCOL.md` for the
protocol. Provisioning is stored in NVS; nothing is hardcoded.

### 1. Run the relay on your laptop

```bash
cd ../controllerplatform
npm install
cp .env.example .env            # set SERVER_SECRET + DEV_PASSWORD
cp devices.example.json devices.json
#   in devices.json, set a unique "secret" for a device id, e.g.:
#     { "id": "esp32-lab-01", "name": "...", "secret": "dev-secret-1", "ownerId": "dev-operator" }
npm run dev                      # listens on 0.0.0.0:8080
```

Find your laptop's LAN IP (e.g. `192.168.1.50`): `ipconfig` (Windows) /
`ifconfig` (macOS/Linux). The ESP32 must be on the **same Wi-Fi**.

### 2. Provision the ESP32 (LAN-only, one time)

The ESP32 must already be on your Wi-Fi (provision Wi-Fi first via its web UI).
Then point it at the relay. From the LAN, POST the cloud config to the device's
web server (replace the device IP and use the same `device_id`/`secret` as in
`devices.json`):

```bash
curl -X POST http://<esp32-ip>/api/cloud/config \
  -H 'Content-Type: application/json' \
  -d '{"uri":"ws://192.168.1.50:8080/ws/device","device_id":"esp32-lab-01","secret":"dev-secret-1"}'
```

The device saves it to NVS and reboots. On boot it dials the relay. Confirm:

```bash
curl http://<esp32-ip>/api/status
# => "cloud_provisioned":true,"cloud_connected":true,"cloud_host":"192.168.1.50:8080",...
```

`idf.py -p <COM> monitor` should show `cloud connected` and
`authenticated; server hello for device_id=esp32-lab-01` (the secret is never
logged).

### 3. Drive the phone through the cloud

Open the relay UI at `http://<laptop-ip>:8080/`, sign in, and the device shows
**online**. Click **Control**, then use the trackpad / click / scroll / keyboard
/ RELEASE ALL — every action is relayed to the ESP32 and applied to the iPhone
over USB HID. Closing the browser tab makes the server tell the device to
`release_all` (verify in the monitor).

Meanwhile the **LAN UI on the ESP32 itself still works** unchanged — cloud and
LAN control coexist.

### 4. Go to a real Internet server

1. Deploy `controllerplatform` to a host with a domain + TLS (a reverse proxy
   such as Caddy/nginx or your cloud load balancer terminating HTTPS/WSS).
2. Re-provision the device with a `wss://` URI:
   `{"uri":"wss://relay.example.com/ws/device","device_id":"...","secret":"..."}`.
   The client verifies the server certificate against the bundled Mozilla root
   CAs. No inbound port is ever opened on the ESP32.

To unprovision: `curl -X POST http://<esp32-ip>/api/cloud/reset`.

