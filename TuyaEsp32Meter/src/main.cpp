/* ============================================================
 *  TuyaEsp32Meter — le devices Tuya pela LAN e publica no MQTT
 *
 *  core 0: DevicePoller (consultas TCP aos devices, bloqueantes)
 *  core 1: loop() com Wi-Fi, pagina web e MQTT
 *
 *  Devices e settings sao editados pela pagina; nada aqui precisa
 *  mudar para cadastrar um device novo.
 * ============================================================ */
#include <Arduino.h>

#include "device_poller.h"
#include "device_store.h"
#include "mqtt_publisher.h"
#include "ota.h"
#include "settings_store.h"
#include "web_api.h"
#include "wifi_manager.h"

namespace {

DeviceStore   store;
DevicePoller  poller(store);
MqttPublisher mqtt(store, poller);

}  // namespace

void setup() {
  Serial.begin(115200);
  delay(200);
  Serial.printf("\n[boot] TuyaEsp32Meter %s\n", FW_VERSION);

  const Settings s = settings::load();
  store.begin();
  Serial.printf("[boot] %u device(s) cadastrado(s), leitura a cada %us\n",
                (unsigned)store.list().size(), s.pollSeconds);

  wifi_manager::begin(s);
  mqtt.begin(s);
  web_api::begin(s, store, poller, mqtt);
  ota::begin(s);
  poller.begin(s.pollSeconds);
}

void loop() {
  wifi_manager::loop();
  ota::loop();
  web_api::loop();
  mqtt.loop();
  delay(2);
}
