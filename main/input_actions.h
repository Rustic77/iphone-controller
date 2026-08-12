/*
 * SPDX-FileCopyrightText: 2026 iphone-controller
 *
 * SPDX-License-Identifier: Unlicense OR CC0-1.0
 *
 * High-level input action layer.
 *
 * This layer expresses human-style gestures (click, double-click, drag, long
 * press, typing, named keys) in terms of the HID controller API in
 * hid_controller.h. It NEVER calls TinyUSB directly.
 *
 * Threading: these functions run in the CALLER's task context and may BLOCK for
 * timed gestures (long press, drag, double click, key taps) using vTaskDelay().
 * They are intended to be driven from a single input-producer context (e.g. the
 * BOOT-button task); concurrent callers should coordinate externally.
 *
 * Safety: every gesture that presses the mouse button guarantees a matching
 * release, even if an intermediate step fails, so a button is never left
 * silently held. input_release_all() is the emergency "let go of everything".
 */
#pragma once

#include <stdint.h>
#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

/* ---- Mouse motion --------------------------------------------------------- */

/** @brief Move the pointer by a relative delta (bounded; see input_actions.c). */
esp_err_t input_move_relative(int16_t dx, int16_t dy);

/** @brief Single left click (DOWN -> short delay -> UP). */
esp_err_t input_click(void);

/** @brief Two left clicks separated by a short configurable interval. */
esp_err_t input_double_click(void);

/** @brief Press and hold the left mouse button. */
esp_err_t input_mouse_down(void);

/** @brief Release the left mouse button. */
esp_err_t input_mouse_up(void);

/**
 * @brief Left button DOWN, hold for duration_ms, then UP.
 * @param duration_ms Hold time; clamped to a safe maximum.
 */
esp_err_t input_long_press(uint32_t duration_ms);

/**
 * @brief Press-drag-release: DOWN, then interpolate the move across many small
 *        reports spread over duration_ms, then UP.
 * @param dx,dy       Total relative movement (bounded).
 * @param duration_ms Time to spread the movement over; clamped to a safe range.
 */
esp_err_t input_drag_relative(int16_t dx, int16_t dy, uint32_t duration_ms);

/** @brief Vertical scroll (positive = up, negative = down). */
esp_err_t input_scroll(int8_t vertical);

/* ---- Keyboard ------------------------------------------------------------- */

/** @brief Type an ASCII string. */
esp_err_t input_type_text(const char *text);

/** @brief Tap the Enter/Return key. */
esp_err_t input_press_enter(void);

/** @brief Tap the Backspace key. */
esp_err_t input_press_backspace(void);

/** @brief Tap the Tab key. */
esp_err_t input_press_tab(void);

/** @brief Tap the Escape key. */
esp_err_t input_press_escape(void);

/* ---- Safety --------------------------------------------------------------- */

/** @brief Emergency release: all mouse buttons, all keys, all modifiers. */
esp_err_t input_release_all(void);

#ifdef __cplusplus
}
#endif
