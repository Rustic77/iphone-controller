/*
 * SPDX-FileCopyrightText: 2026 iphone-controller
 *
 * SPDX-License-Identifier: Unlicense OR CC0-1.0
 *
 * Wi-Fi manager (see wifi_ap.h): provisioning SoftAP + Station mode + NVS creds.
 */

#include "wifi_ap.h"

#include <string.h>
#include <stdio.h>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_log.h"
#include "esp_wifi.h"
#include "esp_event.h"
#include "esp_netif.h"
#include "esp_mac.h"
#include "esp_system.h"
#include "nvs_flash.h"
#include "nvs.h"

#include "input_actions.h"

static const char *TAG = "wifi";

#define WIFI_NVS_NS   "wifi"
#define WIFI_NVS_SSID "ssid"
#define WIFI_NVS_PASS "pass"

static char             s_ap_ssid[33] = {0};    /* our provisioning AP SSID */
static char             s_sta_ssid[33] = {0};   /* target router SSID */
static char             s_sta_ip[16] = {0};
static volatile bool    s_sta_connected = false;
static volatile int     s_sta_retry = 0;
static volatile int     s_ap_clients = 0;
static bool             s_provisioning = false;  /* provisioning AP is up */

/* ===== NVS credential storage ============================================== */

static bool nvs_load_creds(char *ssid, size_t ssid_sz, char *pass, size_t pass_sz)
{
    nvs_handle_t h;
    if (nvs_open(WIFI_NVS_NS, NVS_READONLY, &h) != ESP_OK) {
        return false;
    }
    bool ok = false;
    size_t sl = ssid_sz;
    if (nvs_get_str(h, WIFI_NVS_SSID, ssid, &sl) == ESP_OK && strlen(ssid) > 0) {
        size_t pl = pass_sz;
        if (nvs_get_str(h, WIFI_NVS_PASS, pass, &pl) != ESP_OK) {
            pass[0] = '\0'; /* open network / no password stored */
        }
        ok = true;
    }
    nvs_close(h);
    return ok;
}

bool wifi_has_credentials(void)
{
    char ssid[33] = {0};
    char pass[65] = {0};
    return nvs_load_creds(ssid, sizeof(ssid), pass, sizeof(pass));
}

esp_err_t wifi_save_credentials(const char *ssid, const char *pass)
{
    if (ssid == NULL || strlen(ssid) == 0) {
        return ESP_ERR_INVALID_ARG;
    }
    nvs_handle_t h;
    esp_err_t err = nvs_open(WIFI_NVS_NS, NVS_READWRITE, &h);
    if (err != ESP_OK) {
        return err;
    }
    nvs_set_str(h, WIFI_NVS_SSID, ssid);
    nvs_set_str(h, WIFI_NVS_PASS, pass ? pass : "");
    err = nvs_commit(h);
    nvs_close(h);
    ESP_LOGI(TAG, "saved credentials for SSID: %s", ssid);
    return err;
}

esp_err_t wifi_erase_credentials(void)
{
    nvs_handle_t h;
    esp_err_t err = nvs_open(WIFI_NVS_NS, NVS_READWRITE, &h);
    if (err != ESP_OK) {
        return err;
    }
    nvs_erase_key(h, WIFI_NVS_SSID); /* NOT_FOUND is fine */
    nvs_erase_key(h, WIFI_NVS_PASS);
    err = nvs_commit(h);
    nvs_close(h);
    ESP_LOGW(TAG, "erased stored Wi-Fi credentials");
    return err;
}

void wifi_factory_reset(void)
{
    ESP_LOGW(TAG, "FACTORY RESET: erasing Wi-Fi config and rebooting");
    wifi_erase_credentials();
    vTaskDelay(pdMS_TO_TICKS(200));
    esp_restart();
}

/* ===== Wi-Fi configuration helpers ========================================= */

static void compute_ap_ssid(void)
{
    uint8_t mac[6] = {0};
    esp_read_mac(mac, ESP_MAC_WIFI_SOFTAP);
    snprintf(s_ap_ssid, sizeof(s_ap_ssid), "%s%02X%02X", WIFI_AP_SSID_PREFIX, mac[4], mac[5]);
}

static void wifi_configure_ap(void)
{
    wifi_config_t ap = {0};
    size_t l = strnlen(s_ap_ssid, sizeof(ap.ap.ssid));
    memcpy(ap.ap.ssid, s_ap_ssid, l);
    ap.ap.ssid_len = (uint8_t)l;
    ap.ap.channel = WIFI_AP_CHANNEL;
    ap.ap.max_connection = WIFI_AP_MAX_STA;
    snprintf((char *)ap.ap.password, sizeof(ap.ap.password), "%s", WIFI_AP_PASSWORD);
    ap.ap.authmode = (strlen(WIFI_AP_PASSWORD) > 0) ? WIFI_AUTH_WPA2_PSK : WIFI_AUTH_OPEN;
    ap.ap.pmf_cfg.required = false;
    esp_wifi_set_config(WIFI_IF_AP, &ap);
}

static void wifi_configure_sta(const char *ssid, const char *pass)
{
    wifi_config_t sta = {0};
    snprintf((char *)sta.sta.ssid, sizeof(sta.sta.ssid), "%s", ssid);
    snprintf((char *)sta.sta.password, sizeof(sta.sta.password), "%s", pass ? pass : "");
    /* Leave threshold at default (accept any authmode) for broad compatibility. */
    esp_wifi_set_config(WIFI_IF_STA, &sta);
}

/* Bring the provisioning AP up alongside STA (APSTA) after repeated failures or
 * on manual request. STA keeps auto-reconnecting in the background. */
static esp_err_t wifi_start_provisioning_ap(void)
{
    if (s_provisioning) {
        return ESP_OK;
    }
    ESP_LOGW(TAG, "starting provisioning SoftAP (APSTA)");
    esp_err_t err = esp_wifi_set_mode(WIFI_MODE_APSTA);
    if (err != ESP_OK) {
        return err;
    }
    wifi_configure_ap();
    s_provisioning = true;
    ESP_LOGI(TAG, "SoftAP started");
    ESP_LOGI(TAG, "SSID: %s", s_ap_ssid);
    ESP_LOGI(TAG, "IP address: 192.168.4.1");
    return ESP_OK;
}

/* ===== Event handling ====================================================== */

static void wifi_event_handler(void *arg, esp_event_base_t base, int32_t id, void *data)
{
    (void)arg;

    if (base == WIFI_EVENT) {
        switch (id) {
        case WIFI_EVENT_STA_START:
            esp_wifi_connect();
            break;

        case WIFI_EVENT_STA_DISCONNECTED: {
            bool was_connected = s_sta_connected;
            s_sta_connected = false;
            s_sta_ip[0] = '\0';
            if (was_connected) {
                /* Req 9: a meaningful control-network loss -> release everything. */
                ESP_LOGW(TAG, "STA disconnected -> release all");
                input_release_all();
            }
            s_sta_retry++;
            if (s_sta_retry <= WIFI_STA_MAX_RETRY) {
                ESP_LOGI(TAG, "STA reconnect attempt %d", s_sta_retry);
                esp_wifi_connect();
            } else if (!s_provisioning) {
                ESP_LOGW(TAG, "STA failed %d times -> fall back to provisioning", s_sta_retry);
                wifi_start_provisioning_ap();
                esp_wifi_connect(); /* keep trying STA in the background (req 8) */
            } else {
                esp_wifi_connect(); /* provisioning AP already up; keep retrying */
            }
            break;
        }

        case WIFI_EVENT_AP_STACONNECTED:
            s_ap_clients++;
            ESP_LOGI(TAG, "station connected (clients=%d)", s_ap_clients);
            break;

        case WIFI_EVENT_AP_STADISCONNECTED:
            if (s_ap_clients > 0) {
                s_ap_clients--;
            }
            ESP_LOGI(TAG, "station disconnected (clients=%d)", s_ap_clients);
            break;

        default:
            break;
        }
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        ip_event_got_ip_t *event = (ip_event_got_ip_t *)data;
        snprintf(s_sta_ip, sizeof(s_sta_ip), IPSTR, IP2STR(&event->ip_info.ip));
        s_sta_connected = true;
        s_sta_retry = 0;
        ESP_LOGI(TAG, "STA connected, got IP: %s", s_sta_ip);
    }
}

/* ===== Public API ========================================================== */

esp_err_t wifi_start(void)
{
    esp_err_t ret = nvs_flash_init();
    if (ret == ESP_ERR_NVS_NO_FREE_PAGES || ret == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        ret = nvs_flash_init();
    }
    ESP_ERROR_CHECK(ret);

    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    esp_netif_create_default_wifi_ap();
    esp_netif_create_default_wifi_sta();

    wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&cfg));
    ESP_ERROR_CHECK(esp_event_handler_instance_register(WIFI_EVENT, ESP_EVENT_ANY_ID,
                                                        &wifi_event_handler, NULL, NULL));
    ESP_ERROR_CHECK(esp_event_handler_instance_register(IP_EVENT, IP_EVENT_STA_GOT_IP,
                                                        &wifi_event_handler, NULL, NULL));

    compute_ap_ssid();

    char ssid[33] = {0};
    char pass[65] = {0};
    if (nvs_load_creds(ssid, sizeof(ssid), pass, sizeof(pass))) {
        /* Credentials present -> Station mode. */
        snprintf(s_sta_ssid, sizeof(s_sta_ssid), "%s", ssid);
        ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
        wifi_configure_sta(ssid, pass);
        s_provisioning = false;
        ESP_ERROR_CHECK(esp_wifi_start());
        ESP_LOGI(TAG, "Station mode: connecting to SSID: %s", s_sta_ssid);
    } else {
        /* No credentials -> provisioning SoftAP. */
        ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_AP));
        wifi_configure_ap();
        s_provisioning = true;
        ESP_ERROR_CHECK(esp_wifi_start());
        ESP_LOGI(TAG, "Provisioning mode (no saved Wi-Fi)");
        ESP_LOGI(TAG, "SoftAP started");
        ESP_LOGI(TAG, "SSID: %s", s_ap_ssid);
        ESP_LOGI(TAG, "password: %s", WIFI_AP_PASSWORD);
        ESP_LOGI(TAG, "IP address: 192.168.4.1");
    }
    return ESP_OK;
}

void wifi_get_status(wifi_status_t *out)
{
    memset(out, 0, sizeof(*out));

    wifi_mode_t mode = WIFI_MODE_NULL;
    esp_wifi_get_mode(&mode);
    const char *ms = (mode == WIFI_MODE_STA)   ? "sta"
                   : (mode == WIFI_MODE_AP)    ? "ap"
                   : (mode == WIFI_MODE_APSTA) ? "apsta" : "null";
    snprintf(out->mode, sizeof(out->mode), "%s", ms);

    out->connected = s_sta_connected;
    out->ap_clients = s_ap_clients;

    if (s_sta_connected) {
        snprintf(out->ssid, sizeof(out->ssid), "%s", s_sta_ssid);
        snprintf(out->ip, sizeof(out->ip), "%s", s_sta_ip);
        wifi_ap_record_t ap;
        if (esp_wifi_sta_get_ap_info(&ap) == ESP_OK) {
            out->rssi = ap.rssi;
            out->rssi_valid = true;
        }
    } else if (mode == WIFI_MODE_AP || mode == WIFI_MODE_APSTA) {
        snprintf(out->ssid, sizeof(out->ssid), "%s", s_ap_ssid);
        snprintf(out->ip, sizeof(out->ip), "192.168.4.1");
    } else {
        snprintf(out->ssid, sizeof(out->ssid), "%s", s_sta_ssid);
    }
}

int wifi_ap_get_sta_count(void)
{
    return s_ap_clients;
}
