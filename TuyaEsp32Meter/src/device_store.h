/* ============================================================
 *  device_store.h — devices Tuya cadastrados pela pagina web
 *
 *  Lista persistida na NVS como um JSON. Acesso protegido por mutex:
 *  a pagina (loop) edita e a task de polling le. Quem le recebe uma
 *  copia, nunca referencia para o vetor interno.
 * ============================================================ */
#pragma once
#include <Arduino.h>
#include <ArduinoJson.h>
#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>

#include <vector>

struct DeviceConfig {
  String  id;
  String  name;
  String  ip;
  String  localKey;   // 16 caracteres; nunca sai pela API
  String  profile;
  uint8_t proto = 35;
  bool    enabled = true;
};

class DeviceStore {
 public:
  static constexpr size_t kMaxDevices = 8;

  DeviceStore();
  void begin();

  std::vector<DeviceConfig> list() const;
  bool find(const String &id, DeviceConfig &out) const;

  // Insere ou substitui pelo id. Chave vazia em `cfg` mantem a atual.
  bool upsert(DeviceConfig cfg, String &err);
  bool remove(const String &id);

  // Monta e valida um DeviceConfig a partir do JSON da pagina.
  static bool fromJson(JsonObjectConst in, DeviceConfig &out, String &err);
  static bool validate(const DeviceConfig &cfg, bool requireKey, String &err);
  // Sem a local_key: so `hasKey`.
  static void toJson(const DeviceConfig &cfg, JsonObject out);

 private:
  void loadLocked();
  bool saveLocked() const;

  SemaphoreHandle_t mutex_;
  std::vector<DeviceConfig> devices_;
};
