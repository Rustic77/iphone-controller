/*
 * SPDX-FileCopyrightText: 2026 iphone-controller
 *
 * SPDX-License-Identifier: Unlicense OR CC0-1.0
 *
 * Outbound cloud control client.
 *
 * The ESP32 INITIATES a secure (WSS/TLS) WebSocket connection OUTWARD to a
 * configured relay server and receives the same control protocol the relay
 * speaks to devices. It never opens an inbound Internet-facing port: only this
 * outbound socket carries cloud control, so no port-forwarding / NAT hole is
 * required.
 *
 * Received, validated messages are translated into the existing input_actions
 * API (exactly like the LAN control server), so the two control paths share the
 * same HID plumbing. LAN control keeps working unchanged.
 *
 * Layering:  cloud relay --WSS--> cloud_client (validate) --queue--> worker
 *            --> input_actions / hid_controller --> TinyUSB --> iPhone
 *
 * Security:
 *   - TLS server-certificate verification (Mozilla root bundle) for wss://.
 *   - device_id + a unique device secret, provisioned into NVS (never
 *     hardcoded in source, never logged).
 */
#pragma once

#include <stdbool.h>
#include <stdint.h>
#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Buffer bounds (shared with the NVS provisioning + status snapshot). */
#define CLOUD_URI_MAX        128
#define CLOUD_DEVICE_ID_MAX   64
#define CLOUD_SECRET_MAX      96
#define CLOUD_SESSION_MAX     32

/* Status snapshot for /api/status. NEVER contains the device secret. */
typedef struct {
    bool    provisioned;                         /* a server URI + credentials are stored */
    bool    cloud_connected;                     /* WS is up and authenticated */
    char    cloud_session[CLOUD_SESSION_MAX + 1];/* active control-session id, "" if none */
    int64_t last_cloud_message_ms;               /* ms-since-boot of last received frame, 0 if none */
    char    device_id[CLOUD_DEVICE_ID_MAX + 1];  /* provisioned device id (not secret) */
    char    server_host[CLOUD_URI_MAX + 1];      /* host[:port] from the URI (no scheme/secret) */
} cloud_status_t;

/**
 * @brief Start the cloud client.
 *
 * Loads configuration from NVS. If provisioned, spawns the connection
 * supervisor task (connect -> authenticate -> relay, with heartbeat and
 * exponential-backoff reconnect). If not provisioned, does nothing but leaves
 * the module ready; provision via cloud_client_set_config() then reboot.
 *
 * Call after hid_controller_init() and wifi_start().
 */
esp_err_t cloud_client_start(void);

/**
 * @brief Persist cloud configuration to NVS (namespace "cloud").
 *
 * The secret is stored but never logged or returned by any status call. Takes
 * effect on the next boot (callers typically reboot after provisioning, mirror-
 * ing the Wi-Fi provisioning flow).
 *
 * @param uri        Relay URI, e.g. "wss://relay.example.com/ws/device" (prod)
 *                   or "ws://192.168.1.50:8080/ws/device" (LAN dev).
 * @param device_id  Unique device id registered with the relay.
 * @param secret     Unique per-device secret.
 */
esp_err_t cloud_client_set_config(const char *uri, const char *device_id, const char *secret);

/** @brief Erase stored cloud configuration from NVS. */
esp_err_t cloud_client_erase_config(void);

/** @brief True if a server URI + device credentials are stored in NVS. */
bool cloud_client_is_provisioned(void);

/** @brief Fill a status snapshot (no secrets). Safe to call from any task. */
void cloud_client_get_status(cloud_status_t *out);

#ifdef __cplusplus
}
#endif
