/*
 * SPDX-FileCopyrightText: 2026 iphone-controller
 *
 * SPDX-License-Identifier: Unlicense OR CC0-1.0
 *
 * High-level input action layer (see input_actions.h).
 *
 * Built entirely on top of hid_controller.h. This file must NEVER call TinyUSB
 * directly. Timed gestures pace themselves with vTaskDelay() in the caller's
 * task; the actual reports are transmitted by the HID controller's single
 * worker task, so button/key state stays consistent (each queued move carries
 * the currently-held button state tracked by the controller).
 */

#include "input_actions.h"

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_log.h"
#include "class/hid/hid_device.h"   /* MOUSE_BUTTON_LEFT, HID_KEY_* constants only */
#include "hid_controller.h"

static const char *TAG = "input";

/* ---- Safety bounds / tunables --------------------------------------------- */
#define INPUT_MOVE_MAX          8192   /* max |dx|/|dy| accepted per call */
#define INPUT_DURATION_MAX_MS   10000  /* max hold/drag duration */
#define INPUT_DRAG_MIN_MS       50     /* floor so a drag always interpolates */
#define INPUT_DRAG_STEP_MS      15     /* target interval between drag steps */
#define INPUT_DRAG_MAX_STEPS    512    /* cap on interpolation steps */
#define INPUT_DOUBLE_CLICK_MS   120    /* gap between the two clicks */
#define INPUT_KEY_TAP_MS        20     /* dwell between key DOWN and UP */

/* The mouse button used by all pointer gestures. */
#define INPUT_MOUSE_BUTTON      MOUSE_BUTTON_LEFT

/* ---- Small helpers -------------------------------------------------------- */

static int16_t clamp_move(int16_t v, const char *axis)
{
    if (v > INPUT_MOVE_MAX) {
        ESP_LOGW(TAG, "%s move %d clamped to %d", axis, v, INPUT_MOVE_MAX);
        return INPUT_MOVE_MAX;
    }
    if (v < -INPUT_MOVE_MAX) {
        ESP_LOGW(TAG, "%s move %d clamped to %d", axis, v, -INPUT_MOVE_MAX);
        return -INPUT_MOVE_MAX;
    }
    return v;
}

static uint32_t clamp_duration(uint32_t ms)
{
    if (ms > INPUT_DURATION_MAX_MS) {
        ESP_LOGW(TAG, "duration %u ms clamped to %d ms", (unsigned)ms, INPUT_DURATION_MAX_MS);
        return INPUT_DURATION_MAX_MS;
    }
    return ms;
}

/* Tap a key: DOWN, short dwell, UP. Always attempts UP so a key is never left
 * held, even if the DOWN failed to enqueue. */
static esp_err_t input_tap_key(uint8_t modifiers, uint8_t keycode)
{
    esp_err_t down_err = hid_keyboard_key_down(modifiers, keycode);
    vTaskDelay(pdMS_TO_TICKS(INPUT_KEY_TAP_MS));
    esp_err_t up_err = hid_keyboard_key_up();
    return (down_err != ESP_OK) ? down_err : up_err;
}

/* ---- Mouse motion --------------------------------------------------------- */

esp_err_t input_move_relative(int16_t dx, int16_t dy)
{
    return hid_mouse_move(clamp_move(dx, "x"), clamp_move(dy, "y"));
}

esp_err_t input_click(void)
{
    return hid_mouse_click(INPUT_MOUSE_BUTTON);
}

esp_err_t input_double_click(void)
{
    esp_err_t e1 = hid_mouse_click(INPUT_MOUSE_BUTTON);
    vTaskDelay(pdMS_TO_TICKS(INPUT_DOUBLE_CLICK_MS));
    esp_err_t e2 = hid_mouse_click(INPUT_MOUSE_BUTTON);
    return (e1 != ESP_OK) ? e1 : e2;
}

esp_err_t input_mouse_down(void)
{
    return hid_mouse_button_down(INPUT_MOUSE_BUTTON);
}

esp_err_t input_mouse_up(void)
{
    return hid_mouse_button_up(INPUT_MOUSE_BUTTON);
}

esp_err_t input_long_press(uint32_t duration_ms)
{
    uint32_t dur = clamp_duration(duration_ms);

    esp_err_t down_err = hid_mouse_button_down(INPUT_MOUSE_BUTTON);
    if (down_err != ESP_OK) {
        return down_err; /* nothing is held; safe to bail */
    }

    vTaskDelay(pdMS_TO_TICKS(dur));

    /* Always release, even though we returned success on DOWN. */
    esp_err_t up_err = hid_mouse_button_up(INPUT_MOUSE_BUTTON);
    return up_err;
}

esp_err_t input_drag_relative(int16_t dx, int16_t dy, uint32_t duration_ms)
{
    dx = clamp_move(dx, "x");
    dy = clamp_move(dy, "y");

    uint32_t dur = clamp_duration(duration_ms);
    if (dur < INPUT_DRAG_MIN_MS) {
        dur = INPUT_DRAG_MIN_MS;
    }

    int steps = (int)(dur / INPUT_DRAG_STEP_MS);
    if (steps < 1) {
        steps = 1;
    }
    if (steps > INPUT_DRAG_MAX_STEPS) {
        steps = INPUT_DRAG_MAX_STEPS;
    }
    uint32_t step_delay = dur / (uint32_t)steps;

    ESP_LOGI(TAG, "drag dx=%d dy=%d over %u ms in %d steps", dx, dy, (unsigned)dur, steps);

    /* mouse DOWN */
    esp_err_t down_err = hid_mouse_button_down(INPUT_MOUSE_BUTTON);
    if (down_err != ESP_OK) {
        return down_err; /* nothing held yet */
    }

    /* Gradually move: interpolate the total delta across the steps. Using
     * absolute targets (delta*i/steps) avoids rounding drift and guarantees the
     * final position equals exactly (dx, dy). */
    esp_err_t move_err = ESP_OK;
    int32_t sent_x = 0;
    int32_t sent_y = 0;
    for (int i = 1; i <= steps; i++) {
        int32_t target_x = (int32_t)dx * i / steps;
        int32_t target_y = (int32_t)dy * i / steps;
        int16_t step_dx = (int16_t)(target_x - sent_x);
        int16_t step_dy = (int16_t)(target_y - sent_y);

        if (step_dx != 0 || step_dy != 0) {
            esp_err_t e = hid_mouse_move(step_dx, step_dy);
            if (e != ESP_OK) {
                move_err = e;
                break; /* stop moving, but still release below */
            }
        }
        sent_x = target_x;
        sent_y = target_y;

        if (i < steps) {
            vTaskDelay(pdMS_TO_TICKS(step_delay));
        }
    }

    /* mouse UP -- always attempted so the button is never left held. */
    esp_err_t up_err = hid_mouse_button_up(INPUT_MOUSE_BUTTON);
    return (move_err != ESP_OK) ? move_err : up_err;
}

esp_err_t input_scroll(int8_t vertical)
{
    return hid_mouse_scroll(vertical);
}

/* ---- Keyboard ------------------------------------------------------------- */

esp_err_t input_type_text(const char *text)
{
    return hid_keyboard_type_ascii(text);
}

esp_err_t input_press_enter(void)
{
    return input_tap_key(0, HID_KEY_ENTER);
}

esp_err_t input_press_backspace(void)
{
    return input_tap_key(0, HID_KEY_BACKSPACE);
}

esp_err_t input_press_tab(void)
{
    return input_tap_key(0, HID_KEY_TAB);
}

esp_err_t input_press_escape(void)
{
    return input_tap_key(0, HID_KEY_ESCAPE);
}

/* ---- Safety --------------------------------------------------------------- */

esp_err_t input_release_all(void)
{
    return hid_release_all();
}
