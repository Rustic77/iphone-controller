/*
 * SPDX-FileCopyrightText: 2026 iphone-controller
 *
 * SPDX-License-Identifier: Unlicense OR CC0-1.0
 *
 * Local HTTP control server: embedded browser UI + JSON control API.
 *
 * HTTP handlers NEVER call TinyUSB directly. They only call input_actions or
 * enqueue HID commands via hid_controller. See control_server.c for validation,
 * bounds, rate limiting and the held-input failsafe.
 */
#pragma once

#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * @brief Start the HTTP control server (port 80) and its failsafe watchdog.
 *
 * Call after wifi_ap_start() and hid_controller_init().
 */
esp_err_t control_server_start(void);

#ifdef __cplusplus
}
#endif
