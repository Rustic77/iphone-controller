/*
 * SPDX-FileCopyrightText: 2026 iphone-controller
 *
 * SPDX-License-Identifier: Unlicense OR CC0-1.0
 *
 * Wi-Fi manager: provisioning SoftAP + Station mode with persistent (NVS)
 * credentials.
 *
 * State machine:
 *   no saved creds  -> provisioning SoftAP (serves the web UI incl. a Wi-Fi form)
 *   creds present   -> Station mode, connect to the saved router
 *   repeated STA connect failures -> bring the provisioning SoftAP back up
 *                                    (APSTA) so the user can re-provision.
 *
 * The user's router password is NEVER hardcoded -- it is entered via the web
 * form and stored in NVS. Only the ESP32's own provisioning-AP password is a
 * compile-time constant below.
 */
#pragma once

#include <stdbool.h>
#include <stdint.h>
#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

/* ===== Provisioning SoftAP (the ESP32's OWN access point) ===================
 * This password protects the ESP32's provisioning/control AP. It is NOT the
 * user's home Wi-Fi password (that is provisioned at runtime, never hardcoded).
 * ========================================================================== */
#define WIFI_AP_PASSWORD      "iphonecontrol"
#define WIFI_AP_SSID_PREFIX   "iPhoneController-"
#define WIFI_AP_MAX_STA       4
#define WIFI_AP_CHANNEL       1

/* Consecutive STA connect failures before the provisioning AP is (re)started. */
#define WIFI_STA_MAX_RETRY    15

/* Snapshot of Wi-Fi state for /api/status. Never contains a password. */
typedef struct {
    char    mode[8];      /* "ap", "sta", "apsta", "null" */
    bool    connected;    /* STA has an IP */
    char    ssid[33];     /* active network (STA target) or our AP SSID */
    char    ip[16];       /* current IP address */
    bool    rssi_valid;   /* rssi below is meaningful */
    int8_t  rssi;         /* STA signal strength, when connected */
    int     ap_clients;   /* stations connected to our provisioning AP */
} wifi_status_t;

/** @brief Bring up Wi-Fi: Station mode if creds are saved, else provisioning AP. */
esp_err_t wifi_start(void);

/** @brief True if station credentials are stored in NVS. */
bool wifi_has_credentials(void);

/** @brief Persist station credentials to NVS (does not connect; caller reboots). */
esp_err_t wifi_save_credentials(const char *ssid, const char *pass);

/** @brief Erase stored station credentials from NVS. */
esp_err_t wifi_erase_credentials(void);

/** @brief Erase credentials and reboot into provisioning (factory reset). */
void wifi_factory_reset(void);

/** @brief Fill a status snapshot (no secrets). */
void wifi_get_status(wifi_status_t *out);

/** @brief Number of stations on the provisioning AP (0 in pure STA mode). */
int wifi_ap_get_sta_count(void);

#ifdef __cplusplus
}
#endif
