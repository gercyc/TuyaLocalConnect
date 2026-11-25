/* ============================================================
 *  wifi_manager.h — STA com ponto de acesso de resgate
 *
 *  Sem SSID configurado, ou sem conseguir conectar por kApAfterMs, o
 *  ESP32 abre o AP "TuyaMeter-XXXX" e a pagina fica
 *  em http://192.168.4.1/. O STA segue tentando em paralelo; quando
 *  conecta e ninguem esta no AP, o AP e desligado.
 *
 *  A senha do AP e aleatoria por placa (Settings::apPass), impressa na
 *  serial a cada boot e mostrada nas configuracoes da pagina.
 * ============================================================ */
#pragma once
#include <ArduinoJson.h>

#include "settings_store.h"

namespace wifi_manager {

void begin(const Settings &s);
void loop();
void toJson(JsonObject out);

}  // namespace wifi_manager
