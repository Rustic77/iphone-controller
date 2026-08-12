/*
 * SPDX-FileCopyrightText: 2026 iphone-controller
 *
 * SPDX-License-Identifier: Unlicense OR CC0-1.0
 *
 * Reusable composite HID (mouse + keyboard) controller.
 *
 * All USB HID reports are transmitted by a SINGLE FreeRTOS worker task. Other
 * firmware modules never call TinyUSB directly; they submit commands through the
 * thread-safe API below, which enqueues onto an internal FreeRTOS queue that the
 * worker task drains in FIFO order. See hid_controller.c for the concurrency and
 * state-machine assumptions.
 */
#pragma once

#include <stdint.h>
#include <stdbool.h>
#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * @brief Initialize the HID controller.
 *
 * Creates the HID command queue and starts the single HID worker task. Safe to
 * call once, after tinyusb_driver_install(). Idempotent.
 *
 * @return ESP_OK on success, ESP_ERR_NO_MEM if the queue or task cannot be created.
 */
esp_err_t hid_controller_init(void);

/**
 * @brief Notify the controller of USB mount/unmount.
 *
 * Call from the TinyUSB event callback. On disconnect the worker task clears all
 * internal mouse/keyboard state and drops any pending commands; on reconnect it
 * starts from a fully released state.
 *
 * @param connected true when the host has mounted the device, false on unmount.
 */
void hid_controller_usb_set_connected(bool connected);

/**
 * @brief Whether the USB host currently has the device mounted.
 *
 * Safe to call from any task (e.g. the HTTP server) -- this is the sanctioned
 * way to query USB state without touching TinyUSB directly.
 */
bool hid_controller_usb_mounted(void);

/**
 * @brief Whether the HID IN endpoint is mounted and ready to accept a report.
 */
bool hid_controller_hid_ready(void);

/**
 * @brief Drop every command currently queued but not yet executed.
 *
 * Used when a new control session takes over so stale commands from a previous
 * session can never run. Does not itself release held buttons -- pair it with
 * hid_release_all() when a clean handover is required.
 */
void hid_controller_cancel_pending(void);

/* ---- Mouse ---------------------------------------------------------------- */

/**
 * @brief Move the mouse by a relative delta.
 *
 * dx/dy may exceed a single HID report's range; the worker splits the motion
 * into multiple safe reports. Values are never silently truncated.
 */
esp_err_t hid_mouse_move(int16_t dx, int16_t dy);

/** @brief Press a mouse button (bitmask of TinyUSB MOUSE_BUTTON_*). */
esp_err_t hid_mouse_button_down(uint8_t button);

/** @brief Release a mouse button (bitmask of TinyUSB MOUSE_BUTTON_*). */
esp_err_t hid_mouse_button_up(uint8_t button);

/** @brief Full click: button DOWN, short configurable delay, button UP. */
esp_err_t hid_mouse_click(uint8_t button);

/** @brief Vertical scroll wheel (positive = up, negative = down). */
esp_err_t hid_mouse_scroll(int8_t vertical);

/* ---- Keyboard ------------------------------------------------------------- */

/**
 * @brief Hold a key with modifiers (HID usage keycode + KEYBOARD_MODIFIER_* mask).
 *
 * The key stays held until hid_keyboard_key_up() or hid_release_all() is called.
 */
esp_err_t hid_keyboard_key_down(uint8_t modifiers, uint8_t keycode);

/** @brief Release the currently held key and all modifiers. */
esp_err_t hid_keyboard_key_up(void);

/**
 * @brief Type an ASCII string as a sequence of key taps.
 *
 * Each character is pressed and released. Non-ASCII (>=128) and unmappable
 * characters are skipped rather than corrupting the report stream.
 */
esp_err_t hid_keyboard_type_ascii(const char *text);

/* ---- Safety --------------------------------------------------------------- */

/**
 * @brief Release everything: all mouse buttons, all keyboard keys and modifiers.
 */
esp_err_t hid_release_all(void);

#ifdef __cplusplus
}
#endif
