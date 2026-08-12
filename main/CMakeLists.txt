idf_component_register(
    SRCS "tusb_hid_example_main.c" "hid_controller.c" "input_actions.c" "wifi_ap.c" "control_server.c" "cloud_client.c"
    INCLUDE_DIRS "."
    PRIV_REQUIRES esp_driver_gpio esp_wifi esp_event esp_netif nvs_flash esp_http_server esp_timer
                  esp_websocket_client esp-tls mbedtls
    )
