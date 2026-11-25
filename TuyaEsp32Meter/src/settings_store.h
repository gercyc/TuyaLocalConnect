/* ============================================================
 *  settings_store.h — configuracao geral, persistida na NVS
 *
 *  Wi-Fi, MQTT, intervalo de leitura e senha da pagina. No primeiro boot
 *  (NVS vazia) os valores vem de include/secrets.h, se existir.
 * ============================================================ */
#pragma once
#include <Arduino.h>
#include <ArduinoJson.h>

struct Settings {
  String   wifiSsid;
  String   wifiPass;
  String   mqttHost;
  uint16_t mqttPort = 1883;
  String   mqttUser;
  String   mqttPass;
  String   baseTopic = "tuya32";
  String   discoveryPrefix = "homeassistant";
  uint16_t pollSeconds = 15;
  String   adminPass;   // vazio = pagina sem senha
  String   apPass;      // senha do AP de resgate; gerada no 1o boot
};

namespace settings {

constexpr uint16_t kMinPollSeconds = 5;
constexpr uint16_t kMaxPollSeconds = 3600;

Settings load();
bool save(const Settings &s);

// Apaga settings e devices da NVS (volta ao secrets.h no proximo boot).
void factoryReset();

// JSON para a pagina, com senhas trocadas por um indicador de presenca.
void toJson(const Settings &s, JsonObject out);

// Aplica os campos presentes em `in` sobre `base`. Senha vazia mantem a
// atual (a pagina nunca recebe a senha de volta). Devolve false com
// `err` preenchido se algum campo for invalido.
bool fromJson(JsonObjectConst in, const Settings &base, Settings &out, String &err);

}  // namespace settings
