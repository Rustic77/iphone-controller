/*
 * SPDX-FileCopyrightText: 2022-2025 Espressif Systems (Shanghai) CO LTD
 *
 * SPDX-License-Identifier: Unlicense OR CC0-1.0
 */

#include <stdlib.h>
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "tinyusb.h"
#include "tinyusb_default_config.h"
#include "class/hid/hid_device.h"
#include "driver/gpio.h"
#include "hid_controller.h"
#include "input_actions.h"
#include "wifi_ap.h"
#include "control_server.h"
#include "cloud_client.h"

#define APP_BUTTON (GPIO_NUM_0) // Use BOOT signal by default
static const char *TAG = "example";

/************* TinyUSB descriptors ****************/

#define TUSB_DESC_TOTAL_LEN      (TUD_CONFIG_DESC_LEN + CFG_TUD_HID * TUD_HID_DESC_LEN)

/**
 * @brief HID report descriptor
 *
 * In this example we implement Keyboard + Mouse HID device,
 * so we must define both report descriptors
 */
const uint8_t hid_report_descriptor[] = {
    TUD_HID_REPORT_DESC_KEYBOARD(HID_REPORT_ID(HID_ITF_PROTOCOL_KEYBOARD)),
    TUD_HID_REPORT_DESC_MOUSE(HID_REPORT_ID(HID_ITF_PROTOCOL_MOUSE))
};

/**
 * @brief String descriptor
 */
const char *hid_string_descriptor[5] = {
    // array of pointer to string descriptors
    (char[]){0x09, 0x04},  // 0: is supported language is English (0x0409)
    "TinyUSB",             // 1: Manufacturer
    "TinyUSB Device",      // 2: Product
    "123456",              // 3: Serials, should use chip ID
    "Example HID interface",  // 4: HID
};

/**
 * @brief Configuration descriptor
 *
 * This is a simple configuration descriptor that defines 1 configuration and 1 HID interface
 */
static const uint8_t hid_configuration_descriptor[] = {
    // Configuration number, interface count, string index, total length, attribute, power in mA
    TUD_CONFIG_DESCRIPTOR(1, 1, 0, TUSB_DESC_TOTAL_LEN, TUSB_DESC_CONFIG_ATT_REMOTE_WAKEUP, 100),

    // Interface number, string index, boot protocol, report descriptor len, EP In address, size & polling interval
    TUD_HID_DESCRIPTOR(0, 4, false, sizeof(hid_report_descriptor), 0x81, 16, 10),
};

/********* TinyUSB HID callbacks ***************/

// Invoked when received GET HID REPORT DESCRIPTOR request
// Application return pointer to descriptor, whose contents must exist long enough for transfer to complete
uint8_t const *tud_hid_descriptor_report_cb(uint8_t instance)
{
    // We use only one interface and one HID report descriptor, so we can ignore parameter 'instance'
    return hid_report_descriptor;
}

// Invoked when received GET_REPORT control request
// Application must fill buffer report's content and return its length.
// Return zero will cause the stack to STALL request
uint16_t tud_hid_get_report_cb(uint8_t instance, uint8_t report_id, hid_report_type_t report_type, uint8_t *buffer, uint16_t reqlen)
{
    (void) instance;
    (void) report_id;
    (void) report_type;
    (void) buffer;
    (void) reqlen;

    return 0;
}

// Invoked when received SET_REPORT control request or
// received data on OUT endpoint ( Report ID = 0, Type = 0 )
void tud_hid_set_report_cb(uint8_t instance, uint8_t report_id, hid_report_type_t report_type, uint8_t const *buffer, uint16_t bufsize)
{
}

/********* USB lifecycle ***************/

// esp_tinyusb owns tud_mount_cb()/tud_umount_cb() and forwards them to this
// event callback (registered via TINYUSB_DEFAULT_CONFIG below). Runs in the
// TinyUSB task context. We forward the connection state to the HID controller
// so it can release/clear HID state on disconnect and reconnect.
static void usb_event_cb(tinyusb_event_t *event, void *arg)
{
    switch (event->id) {
    case TINYUSB_EVENT_ATTACHED:
        ESP_LOGI(TAG, "USB mounted");
        hid_controller_usb_set_connected(true);
        break;
    case TINYUSB_EVENT_DETACHED:
        ESP_LOGI(TAG, "USB unmounted");
        hid_controller_usb_set_connected(false);
        break;
    default:
        break;
    }
}

void tud_suspend_cb(bool remote_wakeup_en)
{
    (void) remote_wakeup_en;
    ESP_LOGI(TAG, "USB device suspended");
}

void tud_resume_cb(void)
{
    ESP_LOGI(TAG, "USB device resumed");
}

/********* Application ***************/

// BOOT button (GPIO0) is active-low. A short press performs a mouse move+click;
// a long press types a fixed string. All HID output goes through the HID
// controller queue -- app_main never transmits to TinyUSB directly.
#define BOOT_POLL_MS        10
#define BOOT_DEBOUNCE_MS    20
#define BOOT_LONG_PRESS_MS  600
#define BOOT_FACTORY_MS     5000   // hold >= 5 s to factory-reset Wi-Fi config
#define BOOT_SHORT_MOVE     50

static void handle_boot_short_press(void)
{
    ESP_LOGI(TAG, "BOOT short press: move + click");
    input_move_relative(BOOT_SHORT_MOVE, 0);
    input_click();
}

static void handle_boot_long_press(void)
{
    ESP_LOGI(TAG, "BOOT long press: type text");
    input_type_text("Hello from ESP32");
}

void app_main(void)
{
    ESP_LOGI(TAG, "Firmware started");

    // Initialize button that will trigger HID reports
    const gpio_config_t boot_button_config = {
        .pin_bit_mask = BIT64(APP_BUTTON),
        .mode = GPIO_MODE_INPUT,
        .intr_type = GPIO_INTR_DISABLE,
        .pull_up_en = GPIO_PULLUP_ENABLE,
        .pull_down_en = GPIO_PULLDOWN_DISABLE,
    };
    ESP_ERROR_CHECK(gpio_config(&boot_button_config));

    ESP_LOGI(TAG, "USB initialization");
    // Register usb_event_cb so esp_tinyusb forwards mount/unmount (attach/detach) events to us
    tinyusb_config_t tusb_cfg = TINYUSB_DEFAULT_CONFIG(usb_event_cb);

    tusb_cfg.descriptor.device = NULL;
    tusb_cfg.descriptor.full_speed_config = hid_configuration_descriptor;
    tusb_cfg.descriptor.string = hid_string_descriptor;
    tusb_cfg.descriptor.string_count = sizeof(hid_string_descriptor) / sizeof(hid_string_descriptor[0]);
#if (TUD_OPT_HIGH_SPEED)
    tusb_cfg.descriptor.high_speed_config = hid_configuration_descriptor;
#endif // TUD_OPT_HIGH_SPEED

    ESP_ERROR_CHECK(tinyusb_driver_install(&tusb_cfg));
    ESP_LOGI(TAG, "USB initialization DONE");

    // Start the single HID worker task + command queue
    ESP_ERROR_CHECK(hid_controller_init());

    // Bring up Wi-Fi (Station if provisioned, else provisioning SoftAP) + HTTP server
    ESP_ERROR_CHECK(wifi_start());
    ESP_ERROR_CHECK(control_server_start());

    // Outbound cloud relay client (idle until provisioned via /api/cloud/config).
    // Never opens an inbound port; only dials out. LAN control above is untouched.
    ESP_ERROR_CHECK(cloud_client_start());

    while (1) {
        if (gpio_get_level(APP_BUTTON) == 0) {          // pressed (active-low)
            vTaskDelay(pdMS_TO_TICKS(BOOT_DEBOUNCE_MS)); // debounce
            if (gpio_get_level(APP_BUTTON) == 0) {       // still pressed: confirmed
                // Measure how long the button is held to distinguish short/long
                uint32_t held_ms = BOOT_DEBOUNCE_MS;
                while (gpio_get_level(APP_BUTTON) == 0) {
                    vTaskDelay(pdMS_TO_TICKS(BOOT_POLL_MS));
                    held_ms += BOOT_POLL_MS;
                }
                // Button released; one action per physical press
                if (held_ms >= BOOT_FACTORY_MS) {
                    // Very long hold: factory-reset Wi-Fi config and reboot.
                    ESP_LOGW(TAG, "BOOT held %ums: factory reset Wi-Fi", (unsigned)held_ms);
                    wifi_factory_reset();
                } else if (held_ms >= BOOT_LONG_PRESS_MS) {
                    handle_boot_long_press();
                } else {
                    handle_boot_short_press();
                }
            }
        }
        vTaskDelay(pdMS_TO_TICKS(BOOT_POLL_MS));
    }
}
