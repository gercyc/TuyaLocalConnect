/* ============================================================
 *  device_poller.h — leitura periodica dos devices
 *
 *  Roda numa task FreeRTOS propria: uma consulta Tuya pode levar varios
 *  segundos (connect + handshake + resposta) e nao pode travar a pagina
 *  nem o keepalive do MQTT, que ficam no loop().
 *
 *  O poller nao publica nada: so atualiza o status de cada device. O
 *  MqttPublisher le o snapshot no loop() e publica o que mudou — o
 *  PubSubClient nao e thread-safe, entao so uma task fala com ele.
 * ============================================================ */
#pragma once
#include <Arduino.h>
#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>

#include <map>
#include <vector>

#include "device_store.h"
#include "tuya/tuya_client.h"

struct DeviceStatus {
  String   id;
  bool     online = false;     // ultima leitura ok e menos de kOfflineAfter falhas
  uint16_t failures = 0;       // falhas consecutivas
  uint32_t lastOkMs = 0;       // millis() da ultima leitura ok (0 = nunca)
  uint32_t lastTryMs = 0;
  uint32_t lastDurationMs = 0;
  String   lastError;
  String   stateJson = "{}";   // {"<code>": valor escalado}
  uint32_t version = 0;        // incrementa a cada leitura ok
};

class DevicePoller {
 public:
  // Falhas seguidas antes de declarar o device offline. Uma falha isolada
  // (device ocupado com o app Tuya, por exemplo) nao derruba a entidade.
  static constexpr uint16_t kOfflineAfter = 3;

  explicit DevicePoller(DeviceStore &store);

  void begin(uint16_t pollSeconds);
  void setInterval(uint16_t pollSeconds);

  // Copia do status de todos os devices cadastrados.
  std::vector<DeviceStatus> snapshot() const;

  // Pede leitura imediata (ex.: device recem-salvo).
  void pollSoon(const String &id);

  // Consulta avulsa, sincrona, com uma config que pode nem estar salva.
  // Usada pelo botao "Testar" da pagina. Compartilha o lock das leituras
  // periodicas: o device aceita uma conexao TCP por vez.
  bool probe(const DeviceConfig &cfg, String &rawDpsJson, String &err);

 private:
  static void taskEntry(void *arg);
  void run();
  void pollOne(const DeviceConfig &cfg);
  void pruneRemoved(const std::vector<DeviceConfig> &devices);
  bool query(const DeviceConfig &cfg, JsonDocument &dps, String &err);

  DeviceStore &store_;
  tuya::Client client_;
  SemaphoreHandle_t stateMutex_;
  SemaphoreHandle_t queryMutex_;
  std::map<String, DeviceStatus> status_;
  std::map<String, bool> pollSoon_;
  volatile uint32_t intervalMs_ = 15000;
};
