/* ============================================================
 *  tuya_client.h — consulta de DPs via TCP 6668
 *
 *  Cada consulta abre a conexao, negocia sessao (3.4/3.5), le os DPs e
 *  fecha. O device aceita um cliente TCP por vez, entao quem usa esta
 *  classe precisa serializar as consultas a um mesmo device.
 *
 *  Os buffers ficam no heap, um conjunto por instancia: a instancia nao
 *  e reentrante, mas duas instancias podem rodar em tasks diferentes.
 * ============================================================ */
#pragma once
#include <Arduino.h>
#include <ArduinoJson.h>
#include <WiFiClient.h>

#include <memory>
#include <vector>

#include "tuya_frame.h"

namespace tuya {

struct Endpoint {
  String  ip;
  String  id;                   // devId / gwId
  uint8_t localKey[kKeyLen];
  Proto   proto = 35;           // 33, 34 ou 35
  // DPs a recalcular via UPDATEDPS antes da leitura (medicao que o device
  // so atualiza em degraus). Vazio = so DP_QUERY.
  std::vector<uint8_t> refreshDps;
};

class Client {
 public:
  Client();

  // Le todos os DPs. Em sucesso `dps` recebe o objeto {"<dp>": valor}.
  // Em falha `err` diz o motivo, ja em portugues para a pagina.
  bool queryDps(const Endpoint &ep, JsonDocument &dps, String &err);

 private:
  bool negotiate(WiFiClient &cli, const Endpoint &ep, String &err);
  bool send(WiFiClient &cli, uint32_t cmd, const uint8_t *payload, size_t len,
            const uint8_t *key, Proto proto, String &err);
  bool receive(WiFiClient &cli, const uint8_t *key, Proto proto,
               ParsedFrame &frame, String &err, uint32_t timeoutMs = 5000);
  bool mergeDps(size_t len, JsonDocument &into, String &err);
  void requestRefresh(WiFiClient &cli, const Endpoint &ep, const uint8_t *key,
                      JsonDocument &fresh);

  std::unique_ptr<uint8_t[]> tx_;
  std::unique_ptr<uint8_t[]> rx_;
  std::unique_ptr<uint8_t[]> plain_;
  uint8_t  sessionKey_[kKeyLen] = {};
  uint32_t seq_ = 1;
  uint32_t deadline_ = 0;   // millis() limite da consulta corrente
};

}  // namespace tuya
