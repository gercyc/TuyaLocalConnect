#include "ota.h"

#include <ArduinoOTA.h>
#include <WiFi.h>

namespace ota {
namespace {

String otaPass;
bool started = false;

}  // namespace

const String &password() { return otaPass; }

void begin(const Settings &s) {
  otaPass = s.adminPass.isEmpty() ? s.apPass : s.adminPass;

  String host = "tuya32-" + WiFi.macAddress().substring(12);
  host.replace(":", "");
  ArduinoOTA.setHostname(host.c_str());
  ArduinoOTA.setPassword(otaPass.c_str());
  ArduinoOTA.setMdnsEnabled(true);

  ArduinoOTA.onStart([] { Serial.println("[ota] recebendo firmware..."); });
  ArduinoOTA.onEnd([] { Serial.println("[ota] concluido; reiniciando"); });
  ArduinoOTA.onError([](ota_error_t e) { Serial.printf("[ota] erro %u\n", (unsigned)e); });
}

void loop() {
  // O ArduinoOTA precisa de rede para abrir o socket UDP: inicia na
  // primeira vez que o STA (ou o AP) estiver de pe.
  if (!started) {
    if (WiFi.status() != WL_CONNECTED && WiFi.softAPIP() == IPAddress()) return;
    ArduinoOTA.begin();
    started = true;
    Serial.println("[ota] pronto (porta 3232)");
  }
  ArduinoOTA.handle();
}

}  // namespace ota
