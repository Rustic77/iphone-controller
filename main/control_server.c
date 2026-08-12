/*
 * SPDX-FileCopyrightText: 2026 iphone-controller
 *
 * SPDX-License-Identifier: Unlicense OR CC0-1.0
 *
 * Local HTTP control server (see control_server.h).
 *
 * Layering:  HTTP handler -> input_actions / hid_controller (enqueue) -> TinyUSB
 * Handlers never touch TinyUSB directly.
 *
 * Safety mechanisms in this file:
 *  - JSON body is size-limited, then parsed and type-checked by a small strict
 *    extractor (ESP-IDF 6.0 no longer bundles cJSON). Malformed -> 4xx JSON error.
 *  - All numeric inputs are bounded before use.
 *  - Token-bucket rate limiting on the action endpoints (429 when exceeded).
 *  - HID-queue flooding cannot crash: hid_controller drops non-critical commands
 *    when full; the rate limiter throttles upstream.
 *  - Failsafe watchdog: if a client holds a mouse button and then goes silent,
 *    input_release_all() is issued automatically after a timeout.
 */

#include "control_server.h"

#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/semphr.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "esp_system.h"
#include "esp_http_server.h"

#include "input_actions.h"
#include "hid_controller.h"
#include "wifi_ap.h"
#include "cloud_client.h"

static const char *TAG = "ctrl_srv";

/* ---- Limits / bounds ------------------------------------------------------ */
#define MAX_BODY_LEN         640    /* reject request bodies larger than this */
#define MAX_TEXT_LEN         256    /* reject /api/text longer than this */
#define MOVE_BOUND           2000   /* clamp |dx|,|dy| for /api/move */
#define SCROLL_BOUND         127    /* clamp scroll to int8 range */

/* Token-bucket rate limiter (per whole server). */
#define RL_BUCKET_MAX        40.0f
#define RL_REFILL_PER_SEC    80.0f

/* Failsafe: release everything if a hold goes idle this long. */
#define SAFETY_TIMEOUT_US    (2 * 1000 * 1000)
#define FAILSAFE_PERIOD_MS   500

static httpd_handle_t s_server = NULL;

/* Rate-limiter state (guarded by s_rl_lock). */
static SemaphoreHandle_t s_rl_lock = NULL;
static float   s_rl_tokens = RL_BUCKET_MAX;
static int64_t s_rl_last_us = 0;

/* Failsafe state (single HTTP worker thread + watchdog task; simple volatiles). */
static volatile bool    s_button_held = false;
static volatile int64_t s_last_activity_us = 0;

/* ==========================================================================
 * Embedded browser UI (no external assets, no build system).
 * NOTE: uses single quotes only, so it embeds cleanly in this C string.
 * ========================================================================== */
static const char INDEX_HTML[] =
"<!DOCTYPE html>\n"
"<html><head><meta charset='utf-8'>\n"
"<meta name='viewport' content='width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no'>\n"
"<title>iPhone Controller</title>\n"
"<link rel='icon' href='data:,'>\n"
"<style>\n"
"body{font-family:sans-serif;margin:0;background:#111;color:#eee;text-align:center}\n"
"h1{font-size:20px;padding:10px;margin:0;background:#222}\n"
".status{padding:8px;font-size:14px}\n"
".ok{color:#4caf50}.bad{color:#f44336}\n"
"#pad{margin:10px auto;width:90%;max-width:420px;height:42vh;background:#1e1e2a;border:2px solid #444;border-radius:12px;touch-action:none;display:flex;align-items:center;justify-content:center;color:#666;user-select:none;font-size:18px}\n"
".btns{display:flex;flex-wrap:wrap;gap:8px;justify-content:center;margin:10px}\n"
"button{font-size:16px;padding:12px 16px;border:0;border-radius:8px;background:#3949ab;color:#fff}\n"
"button.warn{background:#b71c1c}\n"
"input{font-size:16px;padding:10px;width:60%;max-width:260px;border-radius:8px;border:1px solid #555;background:#222;color:#eee}\n"
"</style></head><body>\n"
"<h1>iPhone Controller</h1>\n"
"<div class='status'>USB: <span id='usb'>?</span> &nbsp; Controller: <span id='hid'>?</span> &nbsp; Clients: <span id='cli'>?</span> &nbsp; Link: <span id='link'>...</span> &nbsp; v6 tx:<span id='tx'>0</span> rx:<span id='rx'>0</span></div>\n"
"<div id='pad'>TRACKPAD</div>\n"
"<div class='btns'>\n"
"<button onclick='clickBtn()'>LEFT CLICK</button>\n"
"<button onclick='doScroll(1)'>SCROLL UP</button>\n"
"<button onclick='doScroll(-1)'>SCROLL DOWN</button>\n"
"<button class='warn' onclick='releaseAll()'>RELEASE ALL</button>\n"
"</div>\n"
"<div class='btns'>\n"
"<input id='txt' placeholder='type text...'>\n"
"<button onclick='sendText()'>SEND TEXT</button>\n"
"</div>\n"
"<hr style='border-color:#333;margin:16px'>\n"
"<div class='status'>WiFi: <span id='wifi'>...</span></div>\n"
"<div class='btns'>\n"
"<input id='wssid' placeholder='Wi-Fi SSID'>\n"
"<input id='wpass' type='password' placeholder='Wi-Fi password'>\n"
"</div>\n"
"<div class='btns'>\n"
"<button onclick='saveWifi()'>SAVE &amp; CONNECT</button>\n"
"<button class='warn' onclick='resetWifi()'>RESET WIFI</button>\n"
"</div>\n"
"<script>\n"
"function post(u,b){return fetch(u,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(b||{})}).catch(function(){});}\n"
"var session=null,seq=0,ws=null,dragging=false,holdTimer=null,tx=0,rx=0,wsAlive=false;\n"
"var restMap={move:'/api/move',click:'/api/click',down:'/api/mousedown',up:'/api/mouseup',scroll:'/api/scroll',release:'/api/release-all'};\n"
"function setLink(t){var l=document.getElementById('link');if(l)l.textContent=t;}\n"
"function genSession(){var s='';for(var i=0;i<12;i++){s+=Math.floor(Math.random()*16).toString(16);}return s;}\n"
"function connect(){try{ws=new WebSocket('ws://'+location.host+'/ws');}catch(e){setLink('rest');return;}\n"
"ws.onopen=function(){session=genSession();seq=0;wsAlive=false;setLink('ws?');};\n"
"ws.onmessage=function(){rx++;wsAlive=true;setLink('ws');var re=document.getElementById('rx');if(re)re.textContent=rx;};\n"
"ws.onclose=function(){session=null;dragging=false;wsAlive=false;setLink('rest');setTimeout(connect,1000);};\n"
"ws.onerror=function(){try{ws.close();}catch(e){}};}\n"
"connect();\n"
"function act(type,data){data=data||{};tx++;var te=document.getElementById('tx');if(te)te.textContent=tx;\n"
"if(ws&&ws.readyState===1&&session&&wsAlive){var o={};for(var k in data)o[k]=data[k];o.session=session;o.seq=++seq;o.type=type;ws.send(JSON.stringify(o));return;}\n"
"var u=restMap[type];if(u)post(u,data);}\n"
"setInterval(function(){if(ws&&ws.readyState===1&&session){ws.send(JSON.stringify({session:session,seq:++seq,type:'ping'}));}},1000);\n"
"var pad=document.getElementById('pad');\n"
"var active=false,lx=0,ly=0,ax=0,ay=0,moved=0,t0=0;\n"
"pad.addEventListener('pointerdown',function(e){active=true;lx=e.clientX;ly=e.clientY;ax=0;ay=0;moved=0;t0=Date.now();pad.setPointerCapture(e.pointerId);\n"
"holdTimer=setTimeout(function(){if(active&&moved<10){dragging=true;act('down',{button:1});}},500);});\n"
"pad.addEventListener('pointermove',function(e){if(!active)return;var dx=e.clientX-lx,dy=e.clientY-ly;lx=e.clientX;ly=e.clientY;ax+=dx;ay+=dy;moved+=Math.abs(dx)+Math.abs(dy);});\n"
"pad.addEventListener('pointerup',function(e){if(!active)return;active=false;clearTimeout(holdTimer);flush();\n"
"if(dragging){dragging=false;act('up',{button:1});}else if(moved<8&&(Date.now()-t0)<300){act('click',{button:1});}});\n"
"pad.addEventListener('pointercancel',function(){active=false;clearTimeout(holdTimer);dragging=false;act('release');});\n"
"function flush(){var x=Math.round(ax),y=Math.round(ay);ax-=x;ay-=y;if(x!==0||y!==0){act('move',{dx:x,dy:y});}}\n"
"setInterval(function(){if(active)flush();},50);\n"
"function clickBtn(){act('click',{button:1});}\n"
"function doScroll(v){act('scroll',{dy:v*3});}\n"
"function releaseAll(){act('release');}\n"
"window.addEventListener('blur',function(){dragging=false;act('release');});\n"
"document.addEventListener('visibilitychange',function(){if(document.hidden){dragging=false;act('release');}});\n"
"function sendText(){var v=document.getElementById('txt').value;if(v)post('/api/text',{text:v});}\n"
"function saveWifi(){var s=document.getElementById('wssid').value;var p=document.getElementById('wpass').value;if(!s){alert('Enter a Wi-Fi SSID');return;}post('/api/wifi/save',{ssid:s,password:p});alert('Saving. The device will reboot and join your Wi-Fi.');}\n"
"function resetWifi(){if(confirm('Erase saved Wi-Fi and reboot into provisioning?')){post('/api/wifi/reset');alert('Wi-Fi reset. Rebooting into provisioning AP.');}}\n"
"function upd(){fetch('/api/status').then(function(r){return r.json();}).then(function(s){\n"
"var u=document.getElementById('usb');u.textContent=s.usb_mounted?'connected':'disconnected';u.className=s.usb_mounted?'ok':'bad';\n"
"var h=document.getElementById('hid');h.textContent=s.hid_ready?'ready':'not ready';h.className=s.hid_ready?'ok':'bad';\n"
"document.getElementById('cli').textContent=s.wifi_clients;\n"
"var wf=document.getElementById('wifi');if(wf){wf.textContent=s.wifi_mode+' | '+(s.wifi_connected?'connected':'not connected')+' | '+(s.ssid||'-')+' | '+(s.ip_address||'-')+((s.rssi!==null&&s.rssi!==undefined)?(' | '+s.rssi+'dBm'):'');}\n"
"}).catch(function(){});}\n"
"setInterval(upd,1500);upd();\n"
"</script></body></html>\n";

/* ==========================================================================
 * Minimal strict JSON extraction for our fixed, flat schemas.
 * Keys are matched with surrounding quotes ("dx"), so a key is never confused
 * with a longer key ("dx" does not match "d"). Type mismatches are rejected.
 * ========================================================================== */

static const char *skip_ws(const char *p)
{
    while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') {
        p++;
    }
    return p;
}

/* Locate the value that follows "key": . Returns pointer to the first value
 * char, or NULL if the key/colon is absent. */
static const char *json_value_of(const char *json, const char *key)
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
    p = skip_ws(p + n);
    if (*p != ':') {
        return NULL;
    }
    return skip_ws(p + 1);
}

/* True if `key` exists in the object at all. */
static bool json_has_key(const char *json, const char *key)
{
    return json_value_of(json, key) != NULL;
}

/* Extract an integer value. Returns false if the key is missing or its value is
 * not a bare JSON number (e.g. a string or bool -> type error). */
static bool json_get_int(const char *json, const char *key, int *out)
{
    const char *p = json_value_of(json, key);
    if (p == NULL) {
        return false;
    }
    char *end = NULL;
    long v = strtol(p, &end, 10);
    if (end == p) {
        return false; /* not a number */
    }
    *out = (int)v;
    return true;
}

/* Extract a string value into `out` (NUL-terminated), decoding simple escapes.
 * Returns false if the key is missing or its value is not a proper string. */
static bool json_get_string(const char *json, const char *key, char *out, size_t outsize)
{
    const char *p = json_value_of(json, key);
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
            default:  c = e;    break; /* \" \\ \/ and anything else -> literal */
            }
        }
        if (i + 1 >= outsize) {
            return false; /* would overflow; caller treats short buffers as errors */
        }
        out[i++] = c;
    }
    if (*p != '"') {
        return false; /* unterminated string */
    }
    out[i] = '\0';
    return true;
}

/* ==========================================================================
 * Helpers
 * ========================================================================== */

static int bound_int(int v, int lo, int hi)
{
    if (v < lo) {
        return lo;
    }
    if (v > hi) {
        return hi;
    }
    return v;
}

static void mark_activity(void)
{
    s_last_activity_us = esp_timer_get_time();
}

/* Token-bucket: returns true if a request may proceed. */
static bool rate_ok(void)
{
    bool ok = false;
    xSemaphoreTake(s_rl_lock, portMAX_DELAY);
    int64_t now = esp_timer_get_time();
    float dt = (float)(now - s_rl_last_us) / 1000000.0f;
    s_rl_last_us = now;
    s_rl_tokens += dt * RL_REFILL_PER_SEC;
    if (s_rl_tokens > RL_BUCKET_MAX) {
        s_rl_tokens = RL_BUCKET_MAX;
    }
    if (s_rl_tokens >= 1.0f) {
        s_rl_tokens -= 1.0f;
        ok = true;
    }
    xSemaphoreGive(s_rl_lock);
    return ok;
}

static esp_err_t send_json(httpd_req_t *req, const char *status, const char *json)
{
    httpd_resp_set_type(req, "application/json");
    if (status) {
        httpd_resp_set_status(req, status);
    }
    return httpd_resp_sendstr(req, json);
}

static esp_err_t send_error(httpd_req_t *req, const char *status, const char *msg)
{
    char buf[128];
    snprintf(buf, sizeof(buf), "{\"error\":\"%s\"}", msg);
    return send_json(req, status, buf);
}

static esp_err_t send_ok(httpd_req_t *req)
{
    return send_json(req, "200 OK", "{\"ok\":true}");
}

/* Read the full request body into buf (NUL-terminated). Returns:
 *  ESP_OK, ESP_ERR_INVALID_SIZE (too large), or ESP_FAIL (recv error). */
static esp_err_t read_body(httpd_req_t *req, char *buf, size_t bufsize)
{
    size_t len = req->content_len;
    if (len == 0) {
        buf[0] = '\0';
        return ESP_OK;
    }
    if (len >= bufsize) {
        return ESP_ERR_INVALID_SIZE;
    }
    size_t off = 0;
    while (off < len) {
        int r = httpd_req_recv(req, buf + off, len - off);
        if (r <= 0) {
            return ESP_FAIL;
        }
        off += (size_t)r;
    }
    buf[off] = '\0';
    return ESP_OK;
}

/* Common preamble for action endpoints: rate-limit + read body.
 * Returns true if the handler may continue; otherwise it already sent a response. */
static bool begin_action(httpd_req_t *req, char *body, size_t bodysize)
{
    if (!rate_ok()) {
        send_error(req, "429 Too Many Requests", "rate limited");
        return false;
    }
    esp_err_t e = read_body(req, body, bodysize);
    if (e == ESP_ERR_INVALID_SIZE) {
        send_error(req, "413 Payload Too Large", "body too large");
        return false;
    }
    if (e != ESP_OK) {
        send_error(req, "400 Bad Request", "recv failed");
        return false;
    }
    mark_activity();
    return true;
}

/* Parse an optional mouse-button field. Returns 1..7, or -1 if present but invalid. */
static int parse_button(const char *body)
{
    if (!json_has_key(body, "button")) {
        return 1; /* default: left */
    }
    int b;
    if (!json_get_int(body, "button", &b)) {
        return -1; /* present but not a number */
    }
    if (b < 1 || b > 7) {
        return -1;
    }
    return b;
}

/* ==========================================================================
 * WebSocket real-time control
 *
 * All WS state below is touched only from the single esp_http_server task (frame
 * handlers, queued-work callbacks and close_fn all run there), so no locking is
 * needed among them. The failsafe task only reads s_ws_active /
 * s_ws_last_activity_us and may clear s_ws_active.
 * ========================================================================== */

#define WS_MAX_JSON             256          /* max WS text frame we parse */
#define WS_SESSION_LEN          16           /* max chars in a session id */
#define WS_HEARTBEAT_TIMEOUT_US (3 * 1000 * 1000)

static volatile bool    s_ws_active = false;
static int              s_ws_fd = -1;
static char             s_ws_session[WS_SESSION_LEN + 1] = {0};
static uint32_t         s_ws_last_seq = 0;
static volatile int64_t s_ws_last_activity_us = 0;
static int32_t          s_ws_acc_dx = 0;     /* coalesced pending movement */
static int32_t          s_ws_acc_dy = 0;

static int clamp32(int32_t v, int lo, int hi)
{
    if (v < lo) {
        return lo;
    }
    if (v > hi) {
        return hi;
    }
    return (int)v;
}

/* Start a brand-new session on a freshly connected socket. Invalidates any
 * previous session and drops its still-queued commands (req 12/13), then
 * releases anything the old session may have left held. */
static void ws_new_session(int fd)
{
    /* A fresh socket supersedes any previous session: drop its still-queued
     * commands and release anything it left held (req 12/13). The session id is
     * generated by the client and adopted on its first message; s_ws_fd is the
     * real authority for "which connection is in control". */
    hid_controller_cancel_pending();
    input_release_all();
    s_button_held = false;

    s_ws_fd = fd;
    s_ws_session[0] = '\0';   /* not yet adopted */
    s_ws_last_seq = 0;
    s_ws_acc_dx = 0;
    s_ws_acc_dy = 0;
    s_ws_last_activity_us = esp_timer_get_time();
    s_ws_active = true;
}

static void ws_send_pong(httpd_req_t *req)
{
    httpd_ws_frame_t frame = {
        .type = HTTPD_WS_TYPE_TEXT,
        .payload = (uint8_t *)"{\"type\":\"pong\"}",
        .len = 15,
    };
    httpd_ws_send_frame(req, &frame);
}

/* Round-trip ack so the browser can confirm the server actually processed a
 * discrete action (used by the on-page rx counter for diagnosis). */
static void ws_send_ack(httpd_req_t *req)
{
    httpd_ws_frame_t frame = {
        .type = HTTPD_WS_TYPE_TEXT,
        .payload = (uint8_t *)"{\"type\":\"ack\"}",
        .len = 14,
    };
    httpd_ws_send_frame(req, &frame);
}

/* Coalescing move (req 6): accumulate, then try to enqueue. On success subtract
 * only what was actually sent so leftover motion is retried; if the HID queue is
 * full the delta stays accumulated and merges with the next move. */
static void ws_do_move(int dx, int dy)
{
    s_ws_acc_dx = clamp32((int32_t)s_ws_acc_dx + dx, -30000, 30000);
    s_ws_acc_dy = clamp32((int32_t)s_ws_acc_dy + dy, -30000, 30000);
    int16_t sx = (int16_t)clamp32(s_ws_acc_dx, -MOVE_BOUND, MOVE_BOUND);
    int16_t sy = (int16_t)clamp32(s_ws_acc_dy, -MOVE_BOUND, MOVE_BOUND);
    if (sx == 0 && sy == 0) {
        return;
    }
    if (input_move_relative(sx, sy) == ESP_OK) {
        s_ws_acc_dx -= sx;
        s_ws_acc_dy -= sy;
    }
}

static void ws_handle_message(httpd_req_t *req, const char *msg)
{
    /* The controlling connection is identified by its socket fd; only messages
     * on that socket are honored (req 4/12) -- an older still-open socket is
     * ignored no matter what session it claims. */
    if (!s_ws_active || httpd_req_to_sockfd(req) != s_ws_fd) {
        return;
    }

    char type[16];
    char session[WS_SESSION_LEN + 1];
    if (!json_get_string(msg, "type", type, sizeof(type))) {
        return; /* malformed: no type */
    }
    if (!json_get_string(msg, "session", session, sizeof(session))) {
        return; /* malformed: no session */
    }
    /* Adopt the client-generated session id on the first message; after that it
     * must stay consistent. */
    if (s_ws_session[0] == '\0') {
        snprintf(s_ws_session, sizeof(s_ws_session), "%s", session);
    } else if (strcmp(session, s_ws_session) != 0) {
        return;
    }

    int64_t now = esp_timer_get_time();
    s_ws_last_activity_us = now;
    s_last_activity_us = now; /* also feed the shared held-input failsafe */

    int seq;
    if (!json_get_int(msg, "seq", &seq)) {
        return; /* malformed: no seq */
    }
    /* Req 2/3: monotonic seq; ignore duplicates/regressions. Because DOWN/UP
     * always carry a higher seq over ordered TCP, they are never seq-dropped. */
    if ((uint32_t)seq <= s_ws_last_seq) {
        return;
    }
    s_ws_last_seq = (uint32_t)seq;

    if (strcmp(type, "move") == 0) {
        int dx, dy;
        if (json_get_int(msg, "dx", &dx) && json_get_int(msg, "dy", &dy)) {
            ws_do_move(bound_int(dx, -MOVE_BOUND, MOVE_BOUND),
                       bound_int(dy, -MOVE_BOUND, MOVE_BOUND));
        }
    } else if (strcmp(type, "down") == 0) {
        int b = parse_button(msg);
        if (b > 0) {
            ESP_LOGI(TAG, "ws down button=%d", b);
            hid_mouse_button_down((uint8_t)b); /* critical: not dropped */
            s_button_held = true;
        }
        ws_send_ack(req);
    } else if (strcmp(type, "up") == 0) {
        int b = parse_button(msg);
        if (b > 0) {
            ESP_LOGI(TAG, "ws up button=%d", b);
            hid_mouse_button_up((uint8_t)b);   /* critical: not dropped */
            s_button_held = false;
        }
        ws_send_ack(req);
    } else if (strcmp(type, "click") == 0) {
        int b = parse_button(msg);
        if (b > 0) {
            ESP_LOGI(TAG, "ws click button=%d", b);
            hid_mouse_click((uint8_t)b);
        }
        ws_send_ack(req);
    } else if (strcmp(type, "scroll") == 0) {
        int dy;
        if (json_get_int(msg, "dy", &dy)) {
            input_scroll((int8_t)bound_int(dy, -SCROLL_BOUND, SCROLL_BOUND));
        }
        ws_send_ack(req);
    } else if (strcmp(type, "release") == 0) {
        input_release_all();
        s_button_held = false;
        s_ws_acc_dx = 0;
        s_ws_acc_dy = 0;
        ws_send_ack(req);
    } else if (strcmp(type, "ping") == 0) {
        ws_send_pong(req);
    }
    /* unknown types are ignored (session/seq already validated) */
}

static esp_err_t ws_handler(httpd_req_t *req)
{
    if (req->method == HTTP_GET) {
        /* Handshake: a new socket == a new session. */
        int fd = httpd_req_to_sockfd(req);
        ws_new_session(fd);
        ESP_LOGI(TAG, "ws connected (fd=%d)", fd);
        return ESP_OK;
    }

    httpd_ws_frame_t ws_pkt;
    memset(&ws_pkt, 0, sizeof(ws_pkt));
    ws_pkt.type = HTTPD_WS_TYPE_TEXT;

    esp_err_t ret = httpd_ws_recv_frame(req, &ws_pkt, 0); /* fetch length only */
    if (ret != ESP_OK) {
        ESP_LOGW(TAG, "ws recv(len) failed: %s", esp_err_to_name(ret));
        return ret;
    }
    ESP_LOGD(TAG, "ws frame len=%d type=%d", (int)ws_pkt.len, (int)ws_pkt.type);
    if (ws_pkt.len == 0) {
        return ESP_OK;
    }
    if (ws_pkt.len > WS_MAX_JSON) {
        /* Drain oversized frame so the stream stays in sync, then ignore it. */
        uint8_t *tmp = malloc(ws_pkt.len + 1);
        if (tmp != NULL) {
            ws_pkt.payload = tmp;
            httpd_ws_recv_frame(req, &ws_pkt, ws_pkt.len);
            free(tmp);
        }
        return ESP_OK;
    }

    uint8_t buf[WS_MAX_JSON + 1];
    ws_pkt.payload = buf;
    ret = httpd_ws_recv_frame(req, &ws_pkt, ws_pkt.len);
    if (ret != ESP_OK) {
        return ret;
    }
    buf[ws_pkt.len] = '\0';

    if (ws_pkt.type == HTTPD_WS_TYPE_TEXT) {
        ESP_LOGD(TAG, "ws msg: %s", (const char *)buf);
        ws_handle_message(req, (const char *)buf);
    }
    return ESP_OK;
}

/* Called by esp_http_server whenever any socket closes. */
static void ws_close_fn(httpd_handle_t hd, int sockfd)
{
    (void)hd;
    if (s_ws_active && sockfd == s_ws_fd) {
        /* Req 8: release everything the instant the controller drops. */
        ESP_LOGI(TAG, "ws disconnected (fd=%d) -> release all", sockfd);
        input_release_all();
        s_button_held = false;
        s_ws_active = false;
    }
    close(sockfd);
}

/* ==========================================================================
 * Handlers
 * ========================================================================== */

static esp_err_t root_get(httpd_req_t *req)
{
    httpd_resp_set_type(req, "text/html");
    /* Never let the browser cache the UI -- stale JS after a firmware update was
     * a real footgun (old page kept talking to removed endpoints). */
    httpd_resp_set_hdr(req, "Cache-Control", "no-store");
    return httpd_resp_send(req, INDEX_HTML, HTTPD_RESP_USE_STRLEN);
}

static esp_err_t status_get(httpd_req_t *req)
{
    wifi_status_t w;
    wifi_get_status(&w);

    char rssi[12];
    if (w.rssi_valid) {
        snprintf(rssi, sizeof(rssi), "%d", w.rssi);
    } else {
        snprintf(rssi, sizeof(rssi), "null");
    }

    cloud_status_t c;
    cloud_client_get_status(&c);

    char buf[768];
    snprintf(buf, sizeof(buf),
             "{\"usb_mounted\":%s,\"hid_ready\":%s,\"uptime_ms\":%llu,\"wifi_clients\":%d,"
             "\"wifi_mode\":\"%s\",\"wifi_connected\":%s,\"ssid\":\"%s\",\"ip_address\":\"%s\",\"rssi\":%s,"
             "\"cloud_provisioned\":%s,\"cloud_connected\":%s,\"cloud_session\":\"%s\","
             "\"cloud_host\":\"%s\",\"cloud_device_id\":\"%s\",\"last_cloud_message_ms\":%lld}",
             hid_controller_usb_mounted() ? "true" : "false",
             hid_controller_hid_ready() ? "true" : "false",
             (unsigned long long)(esp_timer_get_time() / 1000),
             w.ap_clients, w.mode, w.connected ? "true" : "false",
             w.ssid, w.ip, rssi,
             c.provisioned ? "true" : "false",
             c.cloud_connected ? "true" : "false",
             c.cloud_session, c.server_host, c.device_id,
             (long long)c.last_cloud_message_ms);
    return send_json(req, "200 OK", buf);
}

static esp_err_t move_post(httpd_req_t *req)
{
    char body[MAX_BODY_LEN];
    if (!begin_action(req, body, sizeof(body))) {
        return ESP_OK;
    }
    int dx, dy;
    if (!json_get_int(body, "dx", &dx) || !json_get_int(body, "dy", &dy)) {
        return send_error(req, "400 Bad Request", "dx and dy must be numbers");
    }
    dx = bound_int(dx, -MOVE_BOUND, MOVE_BOUND);
    dy = bound_int(dy, -MOVE_BOUND, MOVE_BOUND);
    input_move_relative((int16_t)dx, (int16_t)dy);
    return send_ok(req);
}

static esp_err_t click_post(httpd_req_t *req)
{
    char body[MAX_BODY_LEN];
    if (!begin_action(req, body, sizeof(body))) {
        return ESP_OK;
    }
    int b = parse_button(body);
    if (b < 0) {
        return send_error(req, "400 Bad Request", "button must be 1..7");
    }
    ESP_LOGI(TAG, "REST click button=%d", b);
    /* Click auto-releases (DOWN->UP), so it never leaves a held state. */
    hid_mouse_click((uint8_t)b);
    return send_ok(req);
}

static esp_err_t mousedown_post(httpd_req_t *req)
{
    char body[MAX_BODY_LEN];
    if (!begin_action(req, body, sizeof(body))) {
        return ESP_OK;
    }
    int b = parse_button(body);
    if (b < 0) {
        return send_error(req, "400 Bad Request", "button must be 1..7");
    }
    hid_mouse_button_down((uint8_t)b);
    s_button_held = true; /* armed for the failsafe watchdog */
    return send_ok(req);
}

static esp_err_t mouseup_post(httpd_req_t *req)
{
    char body[MAX_BODY_LEN];
    if (!begin_action(req, body, sizeof(body))) {
        return ESP_OK;
    }
    int b = parse_button(body);
    if (b < 0) {
        return send_error(req, "400 Bad Request", "button must be 1..7");
    }
    hid_mouse_button_up((uint8_t)b);
    s_button_held = false;
    return send_ok(req);
}

static esp_err_t scroll_post(httpd_req_t *req)
{
    char body[MAX_BODY_LEN];
    if (!begin_action(req, body, sizeof(body))) {
        return ESP_OK;
    }
    int dy;
    if (!json_get_int(body, "dy", &dy)) {
        return send_error(req, "400 Bad Request", "dy must be a number");
    }
    dy = bound_int(dy, -SCROLL_BOUND, SCROLL_BOUND);
    input_scroll((int8_t)dy);
    return send_ok(req);
}

static esp_err_t text_post(httpd_req_t *req)
{
    char body[MAX_BODY_LEN];
    if (!begin_action(req, body, sizeof(body))) {
        return ESP_OK;
    }
    char text[MAX_BODY_LEN];
    if (!json_get_string(body, "text", text, sizeof(text))) {
        return send_error(req, "400 Bad Request", "text must be a string");
    }
    if (strlen(text) > MAX_TEXT_LEN) {
        return send_error(req, "413 Payload Too Large", "text too long");
    }
    input_type_text(text);
    return send_ok(req);
}

static esp_err_t release_all_post(httpd_req_t *req)
{
    char body[MAX_BODY_LEN];
    if (!begin_action(req, body, sizeof(body))) {
        return ESP_OK;
    }
    input_release_all();
    s_button_held = false;
    return send_ok(req);
}

/* ---- Wi-Fi provisioning (config only; never returns stored secrets) ------- */

static void reboot_task(void *arg)
{
    (void)arg;
    vTaskDelay(pdMS_TO_TICKS(1200)); /* let the HTTP response flush first */
    esp_restart();
}

static void schedule_reboot(void)
{
    xTaskCreate(reboot_task, "reboot", 2048, NULL, 5, NULL);
}

static esp_err_t wifi_save_post(httpd_req_t *req)
{
    char body[MAX_BODY_LEN];
    if (!begin_action(req, body, sizeof(body))) {
        return ESP_OK;
    }
    char ssid[33];
    char pass[65];
    if (!json_get_string(body, "ssid", ssid, sizeof(ssid)) || strlen(ssid) == 0) {
        return send_error(req, "400 Bad Request", "ssid required (max 32 chars)");
    }
    if (!json_get_string(body, "password", pass, sizeof(pass))) {
        pass[0] = '\0'; /* allow open networks */
    }
    if (strlen(pass) > 63) {
        return send_error(req, "400 Bad Request", "password too long");
    }
    if (wifi_save_credentials(ssid, pass) != ESP_OK) {
        return send_error(req, "500 Internal Server Error", "save failed");
    }
    ESP_LOGI(TAG, "Wi-Fi provisioned (ssid=%s); rebooting into station mode", ssid);
    send_json(req, "200 OK", "{\"ok\":true,\"rebooting\":true}");
    schedule_reboot();
    return ESP_OK;
}

static esp_err_t wifi_reset_post(httpd_req_t *req)
{
    char body[MAX_BODY_LEN];
    if (!begin_action(req, body, sizeof(body))) {
        return ESP_OK;
    }
    wifi_erase_credentials();
    ESP_LOGW(TAG, "Wi-Fi config reset via web; rebooting into provisioning");
    send_json(req, "200 OK", "{\"ok\":true,\"rebooting\":true}");
    schedule_reboot();
    return ESP_OK;
}

/* ---- Cloud relay provisioning (local-only; secret is write-only) ----------
 * These endpoints are reachable only over the LAN/provisioning AP -- never from
 * the Internet. The device secret is accepted here and stored in NVS by
 * cloud_client; it is never echoed back by /api/status or logged. */

static esp_err_t cloud_config_post(httpd_req_t *req)
{
    char body[MAX_BODY_LEN];
    if (!begin_action(req, body, sizeof(body))) {
        return ESP_OK;
    }
    char uri[CLOUD_URI_MAX + 1];
    char device_id[CLOUD_DEVICE_ID_MAX + 1];
    char secret[CLOUD_SECRET_MAX + 1];
    if (!json_get_string(body, "uri", uri, sizeof(uri)) || strlen(uri) == 0) {
        return send_error(req, "400 Bad Request", "uri required (ws:// or wss://)");
    }
    if (!json_get_string(body, "device_id", device_id, sizeof(device_id)) || strlen(device_id) == 0) {
        return send_error(req, "400 Bad Request", "device_id required");
    }
    if (!json_get_string(body, "secret", secret, sizeof(secret)) || strlen(secret) == 0) {
        return send_error(req, "400 Bad Request", "secret required");
    }
    esp_err_t e = cloud_client_set_config(uri, device_id, secret);
    if (e == ESP_ERR_INVALID_ARG) {
        return send_error(req, "400 Bad Request", "uri must start with ws:// or wss://");
    }
    if (e == ESP_ERR_INVALID_SIZE) {
        return send_error(req, "400 Bad Request", "a field is too long");
    }
    if (e != ESP_OK) {
        return send_error(req, "500 Internal Server Error", "save failed");
    }
    /* Do not log the secret; cloud_client already logs uri + device_id only. */
    send_json(req, "200 OK", "{\"ok\":true,\"rebooting\":true}");
    schedule_reboot();
    return ESP_OK;
}

static esp_err_t cloud_reset_post(httpd_req_t *req)
{
    char body[MAX_BODY_LEN];
    if (!begin_action(req, body, sizeof(body))) {
        return ESP_OK;
    }
    cloud_client_erase_config();
    ESP_LOGW(TAG, "Cloud config reset via web; rebooting");
    send_json(req, "200 OK", "{\"ok\":true,\"rebooting\":true}");
    schedule_reboot();
    return ESP_OK;
}

/* ==========================================================================
 * Failsafe watchdog
 * ========================================================================== */

static void failsafe_task(void *arg)
{
    (void)arg;
    for (;;) {
        vTaskDelay(pdMS_TO_TICKS(FAILSAFE_PERIOD_MS));
        int64_t now = esp_timer_get_time();

        /* Req 10: controlling browser stopped sending heartbeats -> release and
         * force the socket closed so the client reconnects with a new session. */
        if (s_ws_active && (now - s_ws_last_activity_us) > WS_HEARTBEAT_TIMEOUT_US) {
            ESP_LOGW(TAG, "ws heartbeat timeout -> release all");
            input_release_all();
            s_button_held = false;
            s_ws_active = false;
            if (s_server != NULL && s_ws_fd >= 0) {
                httpd_sess_trigger_close(s_server, s_ws_fd);
            }
        }

        /* Held-input failsafe (also covers the REST debug endpoints). */
        if (s_button_held && (now - s_last_activity_us) > SAFETY_TIMEOUT_US) {
            ESP_LOGW(TAG, "failsafe: client idle while holding input -> release all");
            input_release_all();
            s_button_held = false;
        }
    }
}

/* ==========================================================================
 * Start
 * ========================================================================== */

esp_err_t control_server_start(void)
{
    s_rl_lock = xSemaphoreCreateMutex();
    if (s_rl_lock == NULL) {
        return ESP_ERR_NO_MEM;
    }
    s_rl_last_us = esp_timer_get_time();
    s_rl_tokens = RL_BUCKET_MAX;
    s_last_activity_us = esp_timer_get_time();

    httpd_config_t config = HTTPD_DEFAULT_CONFIG();
    config.max_uri_handlers = 16;
    config.stack_size = 8192;
    config.lru_purge_enable = true;
    config.close_fn = ws_close_fn; /* release-all on WS socket close (req 8) */

    esp_err_t err = httpd_start(&s_server, &config);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "httpd_start failed: %s", esp_err_to_name(err));
        return err;
    }

    static const httpd_uri_t uris[] = {
        { .uri = "/",                .method = HTTP_GET,  .handler = root_get },
        { .uri = "/ws",              .method = HTTP_GET,  .handler = ws_handler, .is_websocket = true },
        { .uri = "/api/status",      .method = HTTP_GET,  .handler = status_get },
        { .uri = "/api/move",        .method = HTTP_POST, .handler = move_post },
        { .uri = "/api/click",       .method = HTTP_POST, .handler = click_post },
        { .uri = "/api/mousedown",   .method = HTTP_POST, .handler = mousedown_post },
        { .uri = "/api/mouseup",     .method = HTTP_POST, .handler = mouseup_post },
        { .uri = "/api/scroll",      .method = HTTP_POST, .handler = scroll_post },
        { .uri = "/api/text",        .method = HTTP_POST, .handler = text_post },
        { .uri = "/api/release-all", .method = HTTP_POST, .handler = release_all_post },
        { .uri = "/api/wifi/save",   .method = HTTP_POST, .handler = wifi_save_post },
        { .uri = "/api/wifi/reset",  .method = HTTP_POST, .handler = wifi_reset_post },
        { .uri = "/api/cloud/config", .method = HTTP_POST, .handler = cloud_config_post },
        { .uri = "/api/cloud/reset",  .method = HTTP_POST, .handler = cloud_reset_post },
    };
    for (size_t i = 0; i < sizeof(uris) / sizeof(uris[0]); i++) {
        httpd_register_uri_handler(s_server, &uris[i]);
    }

    xTaskCreate(failsafe_task, "hid_failsafe", 3072, NULL, 4, NULL);

    ESP_LOGI(TAG, "control server started on port %d", config.server_port);
    return ESP_OK;
}
