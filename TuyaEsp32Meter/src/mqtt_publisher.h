/* ============================================================
 *  mqtt_publisher.h — estado dos devices -> MQTT / Home Assistant
 *
 *  Topicos (base = Settings::baseTopic, padrao "tuya32"):
 *    <base>/bridge/availability          online/offline (LWT do ESP32)
 *    <base>/<devId>/availability         online/offline do device Tuya
 *    <base>/<devId>/state                JSON {"<code>": valor}, retain
 *    <disc>/sensor/tuya32_<devId>/<code>/config   discovery, retain
 *
 *  O discovery acompanha a lista de devices sozinho: device novo ganha
 *  entidades, device removido tem as configs apagadas (payload vazio),
 *  nome alterado republica. Quando o HA reinicia (homeassistant/status
 *  = online) tudo e republicado.
 *
 *  Roda so no loop(): o PubSubClient nao e thread-safe.
 * ============================================================ */
#pragma once
#include <Arduino.h>
#include <ArduinoJson.h>
#include <PubSubClient.h>
#include <WiFi.h>

#include <map>
#include <vector>

#include "device_poller.h"
#include "device_store.h"
#include "settings_store.h"

struct DeviceProfile;

class MqttPublisher {
 public:
  MqttPublisher(DeviceStore &store, DevicePoller &poller);

  void begin(const Settings &s);
  void loop();
  void toJson(JsonObject out) const;

 private:
  struct Published {
    String   profile;
    String   signature;        // muda => republica discovery
    uint32_t version = 0;      // ultima versao de estado publicada
    int8_t   online = -1;      // -1 = availability ainda nao publicada
  };

  bool connect();
  void onMessage(char *topic, uint8_t *payload, unsigned int len);
  void syncDiscovery(const std::vector<DeviceConfig> &devices);
  // Devolvem true so se todas as publicacoes sairam.
  bool publishDiscovery(const DeviceConfig &cfg, const DeviceProfile &profile);
  bool removeDiscovery(const String &id, const String &profileId);
  void publishStates(const std::vector<DeviceConfig> &devices);
  bool pub(const String &topic, const String &payload, bool retain = true);

  String deviceTopic(const String &id, const char *leaf) const;
  String nodeId(const String &id) const;

  DeviceStore &store_;
  DevicePoller &poller_;
  WiFiClient net_;
  PubSubClient mqtt_;
  Settings settings_;
  String clientId_;
  String bridgeAvailTopic_;
  std::map<String, Published> published_;

  uint32_t lastAttemptMs_ = 0;
  uint32_t lastSyncMs_ = 0;
  uint32_t publishedOk_ = 0;
  uint32_t publishedFail_ = 0;
  uint32_t connects_ = 0;
  int lastState_ = 0;
};
