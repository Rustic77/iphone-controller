/*
 * SPDX-FileCopyrightText: 2026 iphone-controller
 *
 * SPDX-License-Identifier: Unlicense OR CC0-1.0
 *
 * Composite HID (mouse + keyboard) controller.
 *
 * =============================== ARCHITECTURE ===============================
 *
 *   other firmware modules  (any task)
 *            |  hid_mouse_move / hid_keyboard_type_ascii / hid_release_all ...
 *            v
 *   FreeRTOS command queue  (s_hid_queue, fixed-size items, FIFO)
 *            |
 *            v
 *   single HID worker task  (hid_worker_task)  <-- the ONLY transmitter
 *            |  tud_hid_mouse_report()/tud_hid_keyboard_report()
 *            v
 *   TinyUSB  -->  host (Windows / iPhone)
 *
 * ============================ CONCURRENCY MODEL ============================
 *
 * - Exactly ONE task (the worker) ever calls the TinyUSB tud_hid_* transmit
 *   functions. Public API functions only enqueue; they never touch TinyUSB.
 *   This matches the cross-task pattern used by the original tusb_hid example
 *   (TinyUSB runs its own tud_task; reports are submitted from another task).
 *
 * - The mouse-button / keyboard-modifier / keycode "current state" variables
 *   (s_mouse_buttons, s_kbd_modifiers, s_kbd_keycode) are owned EXCLUSIVELY by
 *   the worker task. No other task reads or writes them, so no lock is needed.
 *
 * - s_usb_connected is the only flag shared across tasks: written by the USB
 *   event callback (TinyUSB task context) via hid_controller_usb_set_connected()
 *   and read by the worker. It is a plain volatile bool with a single writer, so
 *   a simple assignment is sufficient (no lock required).
 *
 * - Ordering is preserved because a single worker drains a FIFO queue. A report
 *   that cannot be sent (endpoint busy / disconnected) is retried or skipped
 *   without reordering later commands.
 */

#include "hid_controller.h"

#include <string.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/queue.h"
#include "esp_log.h"
#include "tinyusb.h"
#include "class/hid/hid_device.h"

static const char *TAG = "hid_ctrl";

/* Report IDs must match the report descriptor in tusb_hid_example_main.c, which
 * uses HID_REPORT_ID(HID_ITF_PROTOCOL_KEYBOARD) and HID_REPORT_ID(HID_ITF_PROTOCOL_MOUSE). */
#define REPORT_ID_KEYBOARD      HID_ITF_PROTOCOL_KEYBOARD
#define REPORT_ID_MOUSE         HID_ITF_PROTOCOL_MOUSE

/* ---- Tunables ------------------------------------------------------------- */
#define HID_QUEUE_LEN           32     /* commands buffered before the queue is full */
#define HID_TEXT_CHUNK_LEN      63     /* ASCII chars carried per TYPE_TEXT command */
#define HID_MOUSE_STEP_MAX      127    /* int8_t report field limit for x/y movement */
#define HID_CLICK_DELAY_MS      50     /* configurable DOWN->UP delay for hid_mouse_click */
#define HID_KEY_TAP_MS          20     /* DOWN/UP dwell time while typing text */
#define HID_READY_TIMEOUT_MS    100    /* max wait for the HID IN endpoint to be ready */
#define HID_ENQUEUE_CRIT_MS     100    /* block time for critical commands when queue full */
#define HID_ENQUEUE_NORM_MS     10     /* block time for non-critical commands when queue full */

/* ---- Command model -------------------------------------------------------- */
typedef enum {
    HID_CMD_MOUSE_MOVE = 0,
    HID_CMD_MOUSE_DOWN,
    HID_CMD_MOUSE_UP,
    HID_CMD_MOUSE_CLICK,
    HID_CMD_MOUSE_SCROLL,
    HID_CMD_KEY_DOWN,
    HID_CMD_KEY_UP,
    HID_CMD_TYPE_TEXT,
    HID_CMD_RELEASE_ALL,
} hid_cmd_type_t;

typedef struct {
    hid_cmd_type_t type;
    union {
        struct { int16_t dx; int16_t dy; } move;
        struct { uint8_t button; } mouse_btn;
        struct { int8_t vertical; } scroll;
        struct { uint8_t modifiers; uint8_t keycode; } key;
        struct { char text[HID_TEXT_CHUNK_LEN + 1]; } type_text;
    };
} hid_cmd_t;

/* ---- Module state --------------------------------------------------------- */
static QueueHandle_t s_hid_queue = NULL;
static TaskHandle_t  s_hid_task  = NULL;

/* Cross-task flag: written by the USB event callback, read by the worker. */
static volatile bool s_usb_connected = false;

/* Worker-task-owned current HID state (see concurrency notes above). */
static uint8_t s_mouse_buttons = 0;   /* pressed mouse-button bitmask */
static uint8_t s_kbd_modifiers = 0;   /* held keyboard modifier bitmask */
static uint8_t s_kbd_keycode   = 0;   /* single held keycode, 0 = none */

/* ASCII -> {shift?, HID keycode} lookup (provided by TinyUSB's class/hid/hid.h). */
static const uint8_t s_ascii2keycode[128][2] = { HID_ASCII_TO_KEYCODE };

/* ==========================================================================
 * Enqueue helpers (run in the CALLER's task context; only touch the queue)
 * ========================================================================== */

/*
 * Critical commands (button/key DOWN/UP, RELEASE_ALL, typed text) must not be
 * silently lost: they block briefly and return an explicit error if the queue is
 * still full. Non-critical commands (move/scroll) tolerate being dropped, but the
 * drop is always logged (never silent).
 */
static esp_err_t hid_enqueue(const hid_cmd_t *cmd, bool critical)
{
    if (s_hid_queue == NULL) {
        return ESP_ERR_INVALID_STATE;
    }

    TickType_t wait = pdMS_TO_TICKS(critical ? HID_ENQUEUE_CRIT_MS : HID_ENQUEUE_NORM_MS);
    if (xQueueSend(s_hid_queue, cmd, wait) != pdTRUE) {
        if (critical) {
            ESP_LOGE(TAG, "queue full: critical cmd %d NOT enqueued", (int)cmd->type);
            return ESP_ERR_TIMEOUT;
        }
        ESP_LOGW(TAG, "queue full: dropped cmd %d", (int)cmd->type);
        return ESP_ERR_NO_MEM;
    }
    return ESP_OK;
}

esp_err_t hid_mouse_move(int16_t dx, int16_t dy)
{
    if (dx == 0 && dy == 0) {
        return ESP_OK;
    }
    hid_cmd_t c = { .type = HID_CMD_MOUSE_MOVE, .move = { dx, dy } };
    return hid_enqueue(&c, false);
}

esp_err_t hid_mouse_button_down(uint8_t button)
{
    hid_cmd_t c = { .type = HID_CMD_MOUSE_DOWN, .mouse_btn = { button } };
    return hid_enqueue(&c, true);
}

esp_err_t hid_mouse_button_up(uint8_t button)
{
    hid_cmd_t c = { .type = HID_CMD_MOUSE_UP, .mouse_btn = { button } };
    return hid_enqueue(&c, true);
}

esp_err_t hid_mouse_click(uint8_t button)
{
    hid_cmd_t c = { .type = HID_CMD_MOUSE_CLICK, .mouse_btn = { button } };
    return hid_enqueue(&c, true);
}

esp_err_t hid_mouse_scroll(int8_t vertical)
{
    hid_cmd_t c = { .type = HID_CMD_MOUSE_SCROLL, .scroll = { vertical } };
    return hid_enqueue(&c, false);
}

esp_err_t hid_keyboard_key_down(uint8_t modifiers, uint8_t keycode)
{
    hid_cmd_t c = { .type = HID_CMD_KEY_DOWN, .key = { modifiers, keycode } };
    return hid_enqueue(&c, true);
}

esp_err_t hid_keyboard_key_up(void)
{
    hid_cmd_t c = { .type = HID_CMD_KEY_UP };
    return hid_enqueue(&c, true);
}

esp_err_t hid_keyboard_type_ascii(const char *text)
{
    if (text == NULL) {
        return ESP_ERR_INVALID_ARG;
    }

    /* Split long strings into fixed-size TYPE_TEXT commands so nothing is
     * truncated by the fixed queue-item size. */
    size_t len = strlen(text);
    size_t off = 0;
    while (off < len) {
        size_t n = len - off;
        if (n > HID_TEXT_CHUNK_LEN) {
            n = HID_TEXT_CHUNK_LEN;
        }
        hid_cmd_t c = { .type = HID_CMD_TYPE_TEXT };
        memcpy(c.type_text.text, text + off, n);
        c.type_text.text[n] = '\0';

        esp_err_t err = hid_enqueue(&c, true);
        if (err != ESP_OK) {
            return err;
        }
        off += n;
    }
    return ESP_OK;
}

esp_err_t hid_release_all(void)
{
    hid_cmd_t c = { .type = HID_CMD_RELEASE_ALL };
    return hid_enqueue(&c, true);
}

/* ==========================================================================
 * Worker-task-only transmit helpers
 * ========================================================================== */

/*
 * Wait (bounded) for the HID IN endpoint to be ready. Returns false if the
 * device disconnects or the endpoint never frees up, so callers can skip the
 * report instead of blocking forever or crashing. Retrying here (rather than
 * dropping) preserves command ordering because the worker is single-threaded.
 */
static bool hid_wait_ready(void)
{
    for (int i = 0; i < HID_READY_TIMEOUT_MS; i++) {
        if (!s_usb_connected) {
            return false;
        }
        if (tud_hid_ready()) {
            return true;
        }
        vTaskDelay(pdMS_TO_TICKS(1));
    }
    return false;
}

static void send_mouse(uint8_t buttons, int8_t x, int8_t y, int8_t vertical, int8_t horizontal)
{
    if (!hid_wait_ready()) {
        ESP_LOGD(TAG, "mouse report skipped (endpoint not ready)");
        return;
    }
    tud_hid_mouse_report(REPORT_ID_MOUSE, buttons, x, y, vertical, horizontal);
}

static void send_keyboard(uint8_t modifiers, uint8_t keycode)
{
    if (!hid_wait_ready()) {
        ESP_LOGD(TAG, "keyboard report skipped (endpoint not ready)");
        return;
    }
    if (keycode != 0) {
        uint8_t keys[6] = { keycode, 0, 0, 0, 0, 0 };
        tud_hid_keyboard_report(REPORT_ID_KEYBOARD, modifiers, keys);
    } else {
        tud_hid_keyboard_report(REPORT_ID_KEYBOARD, modifiers, NULL);
    }
}

/* ==========================================================================
 * Worker-task-only command handlers
 * ========================================================================== */

static void do_mouse_move(int16_t dx, int16_t dy)
{
    /* Break motion that exceeds a single report's int8_t range into multiple
     * safe reports; never truncate. */
    while (dx != 0 || dy != 0) {
        int8_t sx = (dx > HID_MOUSE_STEP_MAX) ? HID_MOUSE_STEP_MAX
                    : (dx < -HID_MOUSE_STEP_MAX) ? -HID_MOUSE_STEP_MAX : (int8_t)dx;
        int8_t sy = (dy > HID_MOUSE_STEP_MAX) ? HID_MOUSE_STEP_MAX
                    : (dy < -HID_MOUSE_STEP_MAX) ? -HID_MOUSE_STEP_MAX : (int8_t)dy;

        send_mouse(s_mouse_buttons, sx, sy, 0, 0);
        dx -= sx;
        dy -= sy;

        if (dx != 0 || dy != 0) {
            vTaskDelay(pdMS_TO_TICKS(2)); /* let the endpoint drain between chunks */
        }
    }
}

static void do_mouse_down(uint8_t button)
{
    s_mouse_buttons |= button;
    send_mouse(s_mouse_buttons, 0, 0, 0, 0);
}

static void do_mouse_up(uint8_t button)
{
    s_mouse_buttons &= (uint8_t)~button;
    send_mouse(s_mouse_buttons, 0, 0, 0, 0);
}

static void do_mouse_click(uint8_t button)
{
    /* A click is always DOWN -> delay -> UP so the button never sticks. */
    s_mouse_buttons |= button;
    send_mouse(s_mouse_buttons, 0, 0, 0, 0);
    vTaskDelay(pdMS_TO_TICKS(HID_CLICK_DELAY_MS));
    s_mouse_buttons &= (uint8_t)~button;
    send_mouse(s_mouse_buttons, 0, 0, 0, 0);
}

static void do_mouse_scroll(int8_t vertical)
{
    send_mouse(s_mouse_buttons, 0, 0, vertical, 0);
}

static void do_key_down(uint8_t modifiers, uint8_t keycode)
{
    s_kbd_modifiers = modifiers;
    s_kbd_keycode = keycode;
    send_keyboard(s_kbd_modifiers, s_kbd_keycode);
}

static void do_key_up(void)
{
    s_kbd_modifiers = 0;
    s_kbd_keycode = 0;
    send_keyboard(0, 0);
}

static void do_type_text(const char *text)
{
    ESP_LOGI(TAG, "typing %d chars", (int)strlen(text));
    for (size_t i = 0; text[i] != '\0'; i++) {
        uint8_t ch = (uint8_t)text[i];
        if (ch >= 128) {
            continue; /* non-ASCII: skip, don't corrupt the stream */
        }
        uint8_t keycode = s_ascii2keycode[ch][1];
        if (keycode == 0) {
            continue; /* unmappable character */
        }
        uint8_t modifiers = s_ascii2keycode[ch][0] ? KEYBOARD_MODIFIER_LEFTSHIFT : 0;

        /* Press */
        s_kbd_modifiers = modifiers;
        s_kbd_keycode = keycode;
        send_keyboard(modifiers, keycode);
        vTaskDelay(pdMS_TO_TICKS(HID_KEY_TAP_MS));

        /* Release (required so a repeated character registers twice) */
        s_kbd_modifiers = 0;
        s_kbd_keycode = 0;
        send_keyboard(0, 0);
        vTaskDelay(pdMS_TO_TICKS(HID_KEY_TAP_MS));
    }
}

static void do_release_all(void)
{
    s_mouse_buttons = 0;
    s_kbd_modifiers = 0;
    s_kbd_keycode = 0;
    send_mouse(0, 0, 0, 0, 0);
    send_keyboard(0, 0);
}

/* Clear tracked state WITHOUT transmitting (used on disconnect, when the device
 * is gone and reports cannot be sent). */
static void reset_state_local(void)
{
    s_mouse_buttons = 0;
    s_kbd_modifiers = 0;
    s_kbd_keycode = 0;
}

/* ==========================================================================
 * Worker task
 * ========================================================================== */

static void hid_worker_task(void *arg)
{
    (void)arg;
    bool was_connected = false;
    hid_cmd_t cmd;

    ESP_LOGI(TAG, "HID worker task started");

    for (;;) {
        /* React to USB connection transitions before processing commands. */
        bool connected = s_usb_connected;
        if (was_connected && !connected) {
            /* Requirement 10: on disconnect, immediately clear state and drop
             * queued commands so stale input is not replayed after reconnect. */
            ESP_LOGI(TAG, "USB disconnected: clearing HID state");
            reset_state_local();
            xQueueReset(s_hid_queue);
        } else if (!was_connected && connected) {
            /* Requirement 11: reconnect always begins fully released. */
            ESP_LOGI(TAG, "USB connected: HID state released");
            reset_state_local();
        }
        was_connected = connected;

        /* Short timeout so connection transitions are handled promptly even
         * when no commands arrive. */
        if (xQueueReceive(s_hid_queue, &cmd, pdMS_TO_TICKS(50)) != pdTRUE) {
            continue;
        }

        /* Device gone: discard the command and keep state released. */
        if (!s_usb_connected) {
            continue;
        }

        switch (cmd.type) {
        case HID_CMD_MOUSE_MOVE:   do_mouse_move(cmd.move.dx, cmd.move.dy);        break;
        case HID_CMD_MOUSE_DOWN:   do_mouse_down(cmd.mouse_btn.button);            break;
        case HID_CMD_MOUSE_UP:     do_mouse_up(cmd.mouse_btn.button);              break;
        case HID_CMD_MOUSE_CLICK:  do_mouse_click(cmd.mouse_btn.button);           break;
        case HID_CMD_MOUSE_SCROLL: do_mouse_scroll(cmd.scroll.vertical);           break;
        case HID_CMD_KEY_DOWN:     do_key_down(cmd.key.modifiers, cmd.key.keycode); break;
        case HID_CMD_KEY_UP:       do_key_up();                                    break;
        case HID_CMD_TYPE_TEXT:    do_type_text(cmd.type_text.text);               break;
        case HID_CMD_RELEASE_ALL:  do_release_all();                               break;
        default:                                                                   break;
        }
    }
}

/* ==========================================================================
 * Public lifecycle
 * ========================================================================== */

void hid_controller_usb_set_connected(bool connected)
{
    /* Single-writer flag; the worker observes the change on its next iteration. */
    s_usb_connected = connected;
}

bool hid_controller_usb_mounted(void)
{
    return s_usb_connected;
}

bool hid_controller_hid_ready(void)
{
    /* tud_hid_ready() reads endpoint state; safe to query from other tasks. */
    return s_usb_connected && tud_hid_ready();
}

void hid_controller_cancel_pending(void)
{
    /* xQueueReset is thread-safe; the worker simply finds an empty queue. */
    if (s_hid_queue != NULL) {
        xQueueReset(s_hid_queue);
    }
}

esp_err_t hid_controller_init(void)
{
    if (s_hid_queue != NULL) {
        return ESP_OK; /* already initialized */
    }

    s_hid_queue = xQueueCreate(HID_QUEUE_LEN, sizeof(hid_cmd_t));
    if (s_hid_queue == NULL) {
        ESP_LOGE(TAG, "failed to create HID queue");
        return ESP_ERR_NO_MEM;
    }

    BaseType_t ok = xTaskCreate(hid_worker_task, "hid_worker", 4096, NULL, 5, &s_hid_task);
    if (ok != pdPASS) {
        vQueueDelete(s_hid_queue);
        s_hid_queue = NULL;
        ESP_LOGE(TAG, "failed to create HID worker task");
        return ESP_ERR_NO_MEM;
    }

    ESP_LOGI(TAG, "HID controller initialized (queue depth %d)", HID_QUEUE_LEN);
    return ESP_OK;
}
