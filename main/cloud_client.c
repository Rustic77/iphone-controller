/*
 * SPDX-FileCopyrightText: 2026 iphone-controller
 *
 * SPDX-License-Identifier: Unlicense OR CC0-1.0
 *
 * Outbound cloud control client -- see cloud_client.h for the contract.
 */

#include <string.h>
#include <stdlib.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/queue.h"
#include "freertos/event_groups.h"

#include "esp_log.h"
#include "esp_timer.h"
#include "esp_random.h"
#include "esp_event.h"
#include "nvs.h"
#include "nvs_flash.h"

#include "esp_websocket_client.h"
#include "esp_crt_bundle.h"

#include "input_actions.h"
#include "hid_controller.h"
#include "cloud_client.h"

static const char *TAG = "cloud";

/* NVS storage for provisioning (req 3/4): never hardcoded in source. */
#define CLOUD_NVS_NAMESPACE  "cloud"
#define CLOUD_NVS_KEY_URI    "uri"
#define CLOUD_NVS_KEY_ID     "dev_id"
#define CLOUD_NVS_KEY_SECRET "secret"

/* Timing knobs. */
#define CLOUD_PING_INTERVAL_SEC   10       /* client->server keepalive ping */
#define CLOUD_PINGPONG_TIMEOUT_SEC 25      /* drop link if no pong within this */
#define CLOUD_NETWORK_TIMEOUT_MS  8000
#define CLOUD_BACKOFF_MIN_MS      1000     /* exponential backoff start */
#define CLOUD_BACKOFF_MAX_MS      30000    /* ... capped here */

/* Bounds for incoming frames / translated commands. */
#define CLOUD_FRAME_MAX     768
#define CLOUD_TEXT_MAX      256
#define CLOUD_CMD_QUEUE_LEN 16

/* Movement / scroll clamps (match the LAN server's bounds). */
#define CLOUD_MOVE_BOUND    2000
#define CLOUD_SCROLL_BOUND  127

/* Event-group bits used to wake the supervisor task. */
#define BIT_CONNECTED   BIT0
#define BIT_DISCONNECT  BIT1

/* ==========================================================================
 * Provisioned configuration (loaded from NVS; secret kept in RAM only).
 * ========================================================================== */

typedef struct {
    char uri[CLOUD_URI_MAX + 1];
    char device_id[CLOUD_DEVICE_ID_MAX + 1];
    char secret[CLOUD_SECRET_MAX + 1];
} cloud_cfg_t;

static cloud_cfg_t s_cfg;   /* zeroed => not provisioned */

/* ==========================================================================
 * Status snapshot (read by the HTTP status handler on another task).
 * ========================================================================== */

static portMUX_TYPE s_status_lock = portMUX_INITIALIZER_UNLOCKED;
static cloud_status_t s_status;   /* guarded by s_status_lock */

static void status_set_connected(bool connected)
{
    taskENTER_CRITICAL(&s_status_lock);
    s_status.cloud_connected = connected;
    taskEXIT_CRITICAL(&s_status_lock);
}

static void status_set_session(const char *session)
{
    taskENTER_CRITICAL(&s_status_lock);
    snprintf(s_status.cloud_session, sizeof(s_status.cloud_session), "%s", session ? session : "");
    taskEXIT_CRITICAL(&s_status_lock);
}

static void status_mark_message(void)
{
    int64_t ms = esp_timer_get_time() / 1000;
    taskENTER_CRITICAL(&s_status_lock);
    s_status.last_cloud_message_ms = ms;
    taskEXIT_CRITICAL(&s_status_lock);
}

void cloud_client_get_status(cloud_status_t *out)
{
    if (!out) {
        return;
    }
    taskENTER_CRITICAL(&s_status_lock);
    *out = s_status;
    taskEXIT_CRITICAL(&s_status_lock);
}

/* Copy host[:port] out of a ws(s):// URI into the status snapshot (no secret,
 * no scheme, no path). Best-effort; used only for display. */
static void status_set_host_from_uri(const char *uri)
{
    const char *p = strstr(uri, "://");
    p = p ? p + 3 : uri;
    char host[CLOUD_URI_MAX + 1];
    size_t i = 0;
    while (p[i] != '\0' && p[i] != '/' && i < sizeof(host) - 1) {
        host[i] = p[i];
        i++;
    }
    host[i] = '\0';
    taskENTER_CRITICAL(&s_status_lock);
    snprintf(s_status.server_host, sizeof(s_status.server_host), "%s", host);
    taskEXIT_CRITICAL(&s_status_lock);
}

/* ==========================================================================
 * Minimal JSON extraction (self-contained; same approach as control_server.c).
 * Our messages are small and fixed-shape. Nested keys (kind/dx/dy/text/...) are
 * unique within a message, so a flat scan is sufficient.
 * ========================================================================== */

static const char *j_skip_ws(const char *p)
{
    while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') {
        p++;
    }
    return p;
}

static const char *j_value_of(const char *json, const char *key)
{
    char pat[24];
    int n = snprintf(pat, sizeof(pat), "\"%s\"", key);
    if (n <= 0 || n >= (int)sizeof(pat)) {
        return NULL;
    }
    const char *p = strstr(json, pat);
    if (p == NULL) {
        return NULL;
    }
    p = j_skip_ws(p + n);
    if (*p != ':') {
        return NULL;
    }
    return j_skip_ws(p + 1);
}

static bool j_get_int(const char *json, const char *key, int *out)
{
    const char *p = j_value_of(json, key);
    if (p == NULL) {
        return false;
    }
    char *end = NULL;
    long v = strtol(p, &end, 10);
    if (end == p) {
        return false;
    }
    *out = (int)v;
    return true;
}

static bool j_get_bool(const char *json, const char *key, bool *out)
{
    const char *p = j_value_of(json, key);
    if (p == NULL) {
        return false;
    }
    if (strncmp(p, "true", 4) == 0) {
        *out = true;
        return true;
    }
    if (strncmp(p, "false", 5) == 0) {
        *out = false;
        return true;
    }
    return false;
}

static bool j_get_string(const char *json, const char *key, char *out, size_t outsize)
{
    const char *p = j_value_of(json, key);
    if (p == NULL || *p != '"') {
        return false;
    }
    p++;
    size_t i = 0;
    while (*p != '\0' && *p != '"') {
        char c = *p++;
        if (c == '\\' && *p != '\0') {
            char e = *p++;
            switch (e) {
            case 'n': c = '\n'; break;
            case 't': c = '\t'; break;
            case 'r': c = '\r'; break;
            case 'b': c = '\b'; break;
            case 'f': c = '\f'; break;
            default:  c = e;    break;
            }
        }
        if (i + 1 >= outsize) {
            return false;
        }
        out[i++] = c;
    }
    if (*p != '"') {
        return false;
    }
    out[i] = '\0';
    return true;
}

static int clamp_int(int v, int lo, int hi)
{
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
}

/* ==========================================================================
 * Translated-command queue + worker task.
 *
 * The WS event task only parses/validates and enqueues (fast). A dedicated
 * worker drains this queue and calls input_actions -- which may BLOCK for timed
 * gestures (typing, key taps). Keeping that off the WS task means blocking a
 * gesture never delays reading frames or answering heartbeats.
 * ========================================================================== */

typedef enum {
    CC_MOVE,
    CC_BTN_DOWN,
    CC_BTN_UP,
    CC_SCROLL,
    CC_TEXT,
    CC_KEY,
    CC_RELEASE_ALL,
} cc_kind_t;

typedef enum { CC_KEY_ENTER, CC_KEY_BKSP, CC_KEY_TAB, CC_KEY_ESC } cc_key_t;

typedef struct {
    cc_kind_t kind;
    int16_t   dx, dy;
    uint8_t   button;             /* TinyUSB mask: 1=left 2=right 4=middle */
    int8_t    scroll;
    cc_key_t  key;
    char      text[CLOUD_TEXT_MAX + 1];
} cc_cmd_t;

static QueueHandle_t     s_cmd_queue;
static EventGroupHandle_t s_events;
static esp_websocket_client_handle_t s_client;
static TaskHandle_t      s_worker_task;
static TaskHandle_t      s_supervisor_task;

/* Session/sequence state -- touched ONLY by the single WS event task, so no
 * locking is needed among these. */
static char     s_active_session[CLOUD_SESSION_MAX + 1];
static uint32_t s_last_seq;

/* Enqueue a command. Movement/scroll are droppable under load; everything else
 * is critical and briefly blocks, mirroring the HID controller's policy. */
static void cmd_enqueue(const cc_cmd_t *cmd)
{
    if (!s_cmd_queue) {
        return;
    }
    bool critical = (cmd->kind != CC_MOVE && cmd->kind != CC_SCROLL);
    TickType_t wait = critical ? pdMS_TO_TICKS(50) : 0;
    if (xQueueSend(s_cmd_queue, cmd, wait) != pdTRUE) {
        ESP_LOGW(TAG, "cmd queue full; dropping kind=%d", (int)cmd->kind);
    }
}

static void worker_task(void *arg)
{
    (void)arg;
    cc_cmd_t cmd;
    for (;;) {
        if (xQueueReceive(s_cmd_queue, &cmd, portMAX_DELAY) != pdTRUE) {
            continue;
        }
        switch (cmd.kind) {
        case CC_MOVE:
            input_move_relative(cmd.dx, cmd.dy);
            break;
        case CC_BTN_DOWN:
            hid_mouse_button_down(cmd.button);
            break;
        case CC_BTN_UP:
            hid_mouse_button_up(cmd.button);
            break;
        case CC_SCROLL:
            input_scroll(cmd.scroll);
            break;
        case CC_TEXT:
            input_type_text(cmd.text);
            break;
        case CC_KEY:
            switch (cmd.key) {
            case CC_KEY_ENTER: input_press_enter();     break;
            case CC_KEY_BKSP:  input_press_backspace();  break;
            case CC_KEY_TAB:   input_press_tab();        break;
            case CC_KEY_ESC:   input_press_escape();     break;
            }
            break;
        case CC_RELEASE_ALL:
            input_release_all();
            break;
        }
    }
}

/* ==========================================================================
 * Protocol handling (WS event task context).
 * ========================================================================== */

static uint8_t button_mask(const char *name)
{
    if (strcmp(name, "left") == 0)   return 1;
    if (strcmp(name, "right") == 0)  return 2;
    if (strcmp(name, "middle") == 0) return 4;
    return 0;
}

/* Reset per-session state (new connection, or server-signalled handover). */
static void session_reset(void)
{
    s_active_session[0] = '\0';
    s_last_seq = 0;
    status_set_session("");
}

/* Translate one validated input event into a queued command. */
static void dispatch_event(const char *event)
{
    char kind[16];
    if (!j_get_string(event, "kind", kind, sizeof(kind))) {
        return; /* invalid: no kind */
    }

    cc_cmd_t cmd;
    memset(&cmd, 0, sizeof(cmd));

    if (strcmp(kind, "move") == 0) {
        int dx, dy;
        if (!j_get_int(event, "dx", &dx) || !j_get_int(event, "dy", &dy)) {
            return;
        }
        cmd.kind = CC_MOVE;
        cmd.dx = (int16_t)clamp_int(dx, -CLOUD_MOVE_BOUND, CLOUD_MOVE_BOUND);
        cmd.dy = (int16_t)clamp_int(dy, -CLOUD_MOVE_BOUND, CLOUD_MOVE_BOUND);
        cmd_enqueue(&cmd);
    } else if (strcmp(kind, "click") == 0) {
        char btn[8];
        bool pressed;
        if (!j_get_string(event, "button", btn, sizeof(btn)) || !j_get_bool(event, "pressed", &pressed)) {
            return;
        }
        uint8_t mask = button_mask(btn);
        if (mask == 0) {
            return;
        }
        cmd.kind = pressed ? CC_BTN_DOWN : CC_BTN_UP;
        cmd.button = mask;
        cmd_enqueue(&cmd);
    } else if (strcmp(kind, "scroll") == 0) {
        int dy;
        if (!j_get_int(event, "dy", &dy)) {
            return;
        }
        cmd.kind = CC_SCROLL;
        cmd.scroll = (int8_t)clamp_int(dy, -CLOUD_SCROLL_BOUND, CLOUD_SCROLL_BOUND);
        cmd_enqueue(&cmd);
    } else if (strcmp(kind, "text") == 0) {
        if (!j_get_string(event, "text", cmd.text, sizeof(cmd.text))) {
            return;
        }
        cmd.kind = CC_TEXT;
        cmd_enqueue(&cmd);
    } else if (strcmp(kind, "key") == 0) {
        char code[16];
        bool pressed;
        if (!j_get_string(event, "code", code, sizeof(code)) || !j_get_bool(event, "pressed", &pressed)) {
            return;
        }
        if (!pressed) {
            return; /* named keys are taps; act on the press edge only */
        }
        cmd.kind = CC_KEY;
        if (strcmp(code, "Enter") == 0)          cmd.key = CC_KEY_ENTER;
        else if (strcmp(code, "Backspace") == 0) cmd.key = CC_KEY_BKSP;
        else if (strcmp(code, "Tab") == 0)       cmd.key = CC_KEY_TAB;
        else if (strcmp(code, "Escape") == 0)    cmd.key = CC_KEY_ESC;
        else return; /* unsupported key */
        cmd_enqueue(&cmd);
    } else if (strcmp(kind, "release_all") == 0) {
        cmd.kind = CC_RELEASE_ALL;
        cmd_enqueue(&cmd);
    }
    /* unknown kinds ignored */
}

static void handle_input(const char *msg)
{
    char session[CLOUD_SESSION_MAX + 1];
    int seq;
    if (!j_get_string(msg, "session", session, sizeof(session))) {
        return; /* invalid */
    }
    if (!j_get_int(msg, "seq", &seq)) {
        return; /* invalid */
    }

    /* Session gate (req 11): adopt the first session seen after a reset; reject
     * any other. A server-sent release_all always precedes a legitimate change
     * of session, which is what resets us to "adopt next". */
    if (s_active_session[0] == '\0') {
        snprintf(s_active_session, sizeof(s_active_session), "%s", session);
        status_set_session(s_active_session);
        s_last_seq = 0;
    } else if (strcmp(session, s_active_session) != 0) {
        ESP_LOGW(TAG, "drop: wrong session");
        return;
    }

    /* Sequence gate (req 10/11): strictly increasing; drop duplicate/stale. */
    if ((uint32_t)seq <= s_last_seq) {
        return;
    }
    s_last_seq = (uint32_t)seq;

    /* The event object is nested; our flat scan finds its keys directly. */
    const char *event = strstr(msg, "\"event\"");
    if (!event) {
        return;
    }
    dispatch_event(event);
}

static void handle_frame(const char *data, int len)
{
    if (len <= 0 || len >= CLOUD_FRAME_MAX) {
        return; /* invalid / oversized */
    }
    char buf[CLOUD_FRAME_MAX];
    memcpy(buf, data, len);
    buf[len] = '\0';

    char type[16];
    if (!j_get_string(buf, "type", type, sizeof(type))) {
        return; /* invalid: no type */
    }

    if (strcmp(type, "input") == 0) {
        handle_input(buf);
    } else if (strcmp(type, "release_all") == 0) {
        /* Authoritative release + session handover boundary (req 12/13). */
        cc_cmd_t cmd = { .kind = CC_RELEASE_ALL };
        cmd_enqueue(&cmd);
        session_reset();
    } else if (strcmp(type, "hello") == 0) {
        char id[CLOUD_DEVICE_ID_MAX + 1];
        if (j_get_string(buf, "deviceId", id, sizeof(id))) {
            ESP_LOGI(TAG, "authenticated; server hello for device_id=%s", id);
        }
    } else if (strcmp(type, "ping") == 0) {
        esp_websocket_client_send_text(s_client, "{\"type\":\"pong\"}", 15, pdMS_TO_TICKS(1000));
    }
    /* unknown types ignored */
}

/* ==========================================================================
 * WebSocket events (WS client task context).
 * ========================================================================== */

static void ws_event_handler(void *arg, esp_event_base_t base, int32_t event_id, void *event_data)
{
    (void)arg;
    (void)base;
    esp_websocket_event_data_t *data = (esp_websocket_event_data_t *)event_data;

    switch (event_id) {
    case WEBSOCKET_EVENT_CONNECTED:
        /* Auth is via the handshake headers, so a connect == authenticated. */
        ESP_LOGI(TAG, "cloud connected");
        session_reset();
        status_set_connected(true);
        status_mark_message();
        /* Start from a clean HID state; never carry state across connections. */
        {
            cc_cmd_t cmd = { .kind = CC_RELEASE_ALL };
            cmd_enqueue(&cmd);
        }
        xEventGroupSetBits(s_events, BIT_CONNECTED);
        break;

    case WEBSOCKET_EVENT_DATA:
        status_mark_message();
        if (data && data->op_code == 0x01 /* text */ &&
            data->payload_offset == 0 && data->data_len == data->payload_len) {
            handle_frame(data->data_ptr, data->data_len);
        }
        break;

    case WEBSOCKET_EVENT_DISCONNECTED:
    case WEBSOCKET_EVENT_CLOSED:
    case WEBSOCKET_EVENT_ERROR:
        ESP_LOGW(TAG, "cloud link down (event %d)", (int)event_id);
        status_set_connected(false);
        session_reset();
        /* Req 12: the operator is gone -- drop every held button/key NOW. */
        input_release_all();
        xEventGroupSetBits(s_events, BIT_DISCONNECT);
        break;

    default:
        break;
    }
}

/* ==========================================================================
 * Connection supervisor: connect, then reconnect with exponential backoff.
 * ========================================================================== */

/* Header buffer must outlive the client; it holds the secret and is NEVER
 * logged. Lives for the process once provisioned. */
static char s_headers[CLOUD_DEVICE_ID_MAX + CLOUD_SECRET_MAX + 48];

static void supervisor_task(void *arg)
{
    (void)arg;

    snprintf(s_headers, sizeof(s_headers),
             "x-device-id: %s\r\nx-device-secret: %s\r\n",
             s_cfg.device_id, s_cfg.secret);

    esp_websocket_client_config_t cfg = {
        .uri = s_cfg.uri,
        .headers = s_headers,
        .disable_auto_reconnect = true,          /* we own reconnect (backoff) */
        .crt_bundle_attach = esp_crt_bundle_attach, /* req 1: TLS cert verify (wss) */
        .ping_interval_sec = CLOUD_PING_INTERVAL_SEC,
        .pingpong_timeout_sec = CLOUD_PINGPONG_TIMEOUT_SEC,
        .network_timeout_ms = CLOUD_NETWORK_TIMEOUT_MS,
        .task_stack = 6144,
        .buffer_size = CLOUD_FRAME_MAX,
    };

    s_client = esp_websocket_client_init(&cfg);
    if (!s_client) {
        ESP_LOGE(TAG, "failed to init websocket client");
        vTaskDelete(NULL);
        return;
    }
    esp_websocket_register_events(s_client, WEBSOCKET_EVENT_ANY, ws_event_handler, NULL);

    ESP_LOGI(TAG, "connecting to cloud host=%s device_id=%s", s_status.server_host, s_cfg.device_id);
    uint32_t backoff = CLOUD_BACKOFF_MIN_MS;
    esp_websocket_client_start(s_client);

    for (;;) {
        EventBits_t bits = xEventGroupWaitBits(
            s_events, BIT_CONNECTED | BIT_DISCONNECT, pdTRUE, pdFALSE, portMAX_DELAY);

        if (bits & BIT_CONNECTED) {
            backoff = CLOUD_BACKOFF_MIN_MS; /* healthy: reset backoff */
        }
        if (bits & BIT_DISCONNECT) {
            uint32_t jitter = esp_random() % 500;
            uint32_t delay = backoff + jitter;
            ESP_LOGW(TAG, "reconnecting in %u ms", (unsigned)delay);
            esp_websocket_client_stop(s_client);
            vTaskDelay(pdMS_TO_TICKS(delay));
            backoff = (backoff * 2 > CLOUD_BACKOFF_MAX_MS) ? CLOUD_BACKOFF_MAX_MS : backoff * 2;
            esp_websocket_client_start(s_client);
        }
    }
}

/* ==========================================================================
 * NVS provisioning
 * ========================================================================== */

static esp_err_t cfg_load(cloud_cfg_t *out)
{
    memset(out, 0, sizeof(*out));
    nvs_handle_t h;
    esp_err_t err = nvs_open(CLOUD_NVS_NAMESPACE, NVS_READONLY, &h);
    if (err != ESP_OK) {
        return err; /* namespace absent => not provisioned */
    }
    size_t n;
    n = sizeof(out->uri);       nvs_get_str(h, CLOUD_NVS_KEY_URI, out->uri, &n);
    n = sizeof(out->device_id); nvs_get_str(h, CLOUD_NVS_KEY_ID, out->device_id, &n);
    n = sizeof(out->secret);    nvs_get_str(h, CLOUD_NVS_KEY_SECRET, out->secret, &n);
    nvs_close(h);
    return ESP_OK;
}

static bool cfg_is_complete(const cloud_cfg_t *c)
{
    return c->uri[0] && c->device_id[0] && c->secret[0];
}

esp_err_t cloud_client_set_config(const char *uri, const char *device_id, const char *secret)
{
    if (!uri || !device_id || !secret) {
        return ESP_ERR_INVALID_ARG;
    }
    if (strlen(uri) > CLOUD_URI_MAX || strlen(device_id) > CLOUD_DEVICE_ID_MAX ||
        strlen(secret) > CLOUD_SECRET_MAX) {
        return ESP_ERR_INVALID_SIZE;
    }
    if (strncmp(uri, "ws://", 5) != 0 && strncmp(uri, "wss://", 6) != 0) {
        return ESP_ERR_INVALID_ARG;
    }

    nvs_handle_t h;
    esp_err_t err = nvs_open(CLOUD_NVS_NAMESPACE, NVS_READWRITE, &h);
    if (err != ESP_OK) {
        return err;
    }
    err = nvs_set_str(h, CLOUD_NVS_KEY_URI, uri);
    if (err == ESP_OK) err = nvs_set_str(h, CLOUD_NVS_KEY_ID, device_id);
    if (err == ESP_OK) err = nvs_set_str(h, CLOUD_NVS_KEY_SECRET, secret);
    if (err == ESP_OK) err = nvs_commit(h);
    nvs_close(h);

    /* Never log the secret. */
    ESP_LOGI(TAG, "cloud config saved (uri=%s device_id=%s); reboot to apply", uri, device_id);
    return err;
}

esp_err_t cloud_client_erase_config(void)
{
    nvs_handle_t h;
    esp_err_t err = nvs_open(CLOUD_NVS_NAMESPACE, NVS_READWRITE, &h);
    if (err != ESP_OK) {
        return err;
    }
    nvs_erase_all(h);
    err = nvs_commit(h);
    nvs_close(h);
    memset(&s_cfg, 0, sizeof(s_cfg));
    memset(s_headers, 0, sizeof(s_headers));
    ESP_LOGI(TAG, "cloud config erased");
    return err;
}

bool cloud_client_is_provisioned(void)
{
    return cfg_is_complete(&s_cfg);
}

esp_err_t cloud_client_start(void)
{
    /* Initialize the status snapshot up front so callers get sane values even
     * before (or without) provisioning. */
    memset(&s_status, 0, sizeof(s_status));

    cfg_load(&s_cfg);

    taskENTER_CRITICAL(&s_status_lock);
    s_status.provisioned = cfg_is_complete(&s_cfg);
    snprintf(s_status.device_id, sizeof(s_status.device_id), "%s", s_cfg.device_id);
    taskEXIT_CRITICAL(&s_status_lock);

    if (!cfg_is_complete(&s_cfg)) {
        ESP_LOGI(TAG, "cloud client idle: not provisioned "
                      "(POST /api/cloud/config to enable, then reboot)");
        return ESP_OK;
    }
    status_set_host_from_uri(s_cfg.uri);

    s_events = xEventGroupCreate();
    s_cmd_queue = xQueueCreate(CLOUD_CMD_QUEUE_LEN, sizeof(cc_cmd_t));
    if (!s_events || !s_cmd_queue) {
        ESP_LOGE(TAG, "failed to allocate cloud client resources");
        return ESP_ERR_NO_MEM;
    }

    if (xTaskCreate(worker_task, "cloud_worker", 4096, NULL, 5, &s_worker_task) != pdPASS ||
        xTaskCreate(supervisor_task, "cloud_super", 6144, NULL, 5, &s_supervisor_task) != pdPASS) {
        ESP_LOGE(TAG, "failed to create cloud client tasks");
        return ESP_ERR_NO_MEM;
    }
    return ESP_OK;
}
