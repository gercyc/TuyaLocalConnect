#include "wifi_manager.h"

#include <WiFi.h>

namespace wifi_manager {
namespace {

constexpr uint32_t kApAfterMs     = 20000;   // sem STA por isso => abre o AP
constexpr uint32_t kApCloseAfterMs = 30000;  // STA estavel por isso => fecha o AP

bool     hasSsid = false;
bool     apOn = false;
uint32_t disconnectedSince = 0;
uint32_t connectedSince = 0;
String   apName;
String   apPass;

void startAp() {
  WiFi.mode(hasSsid ? WIFI_AP_STA : WIFI_AP);
  WiFi.softAP(apName.c_str(), apPass.c_str());
  apOn = true;
  Serial.printf("[wifi] AP '%s' ativo em %s\n", apName.c_str(),
                WiFi.softAPIP().toString().c_str());
}

void stopAp() {
  WiFi.softAPdisconnect(true);
  WiFi.mode(WIFI_STA);
  apOn = false;
  Serial.println("[wifi] AP desligado (STA conectado)");
}

}  // namespace

void begin(const Settings &s) {
  String suffix = WiFi.macAddress().substring(12);
  suffix.replace(":", "");
  apName = "TuyaMeter-" + suffix;
  hasSsid = !s.wifiSsid.isEmpty();
  apPass = s.apPass;
  Serial.printf("[wifi] AP de resgate: '%s' senha '%s'\n", apName.c_str(), apPass.c_str());

  WiFi.persistent(false);   // credenciais ficam na nossa NVS, nao na do WiFi
  WiFi.setHostname(("tuya32-" + suffix).c_str());
  disconnectedSince = millis();

  if (!hasSsid) {
    startAp();
    return;
  }
  WiFi.mode(WIFI_STA);
  WiFi.setAutoReconnect(true);
  WiFi.begin(s.wifiSsid.c_str(), s.wifiPass.c_str());
  Serial.printf("[wifi] conectando a '%s'...\n", s.wifiSsid.c_str());
}

void loop() {
  const uint32_t now = millis();
  const bool connected = WiFi.status() == WL_CONNECTED;

  if (connected) {
    if (connectedSince == 0) {
      connectedSince = now;
      Serial.printf("[wifi] conectado: %s (RSSI %d)\n",
                    WiFi.localIP().toString().c_str(), WiFi.RSSI());
    }
    disconnectedSince = 0;
    if (apOn && now - connectedSince > kApCloseAfterMs && WiFi.softAPgetStationNum() == 0)
      stopAp();
    return;
  }

  if (connectedSince != 0) Serial.println("[wifi] conexao perdida");
  connectedSince = 0;
  if (disconnectedSince == 0) disconnectedSince = now;
  if (!apOn && now - disconnectedSince > kApAfterMs) startAp();
}

void toJson(JsonObject out) {
  const bool connected = WiFi.status() == WL_CONNECTED;
  out["connected"] = connected;
  out["ssid"] = WiFi.SSID();
  out["ip"] = connected ? WiFi.localIP().toString() : String("");
  out["rssi"] = connected ? WiFi.RSSI() : 0;
  out["apActive"] = apOn;
  out["apName"] = apName;
  out["apIp"] = apOn ? WiFi.softAPIP().toString() : String("");
  out["mac"] = WiFi.macAddress();
}

}  // namespace wifi_manager
