/* ============================================================
 *  ota.h — atualizacao de firmware pela rede (ArduinoOTA / espota)
 *
 *    pio run -e esp32dev-ota -t upload
 *
 *  Sempre com senha: a da pagina (adminPass), se houver; senao a do AP
 *  de resgate (apPass), que e aleatoria por placa e aparece na serial e
 *  em Configuracoes. O upload do .bin pela pagina fica em web_api.cpp.
 * ============================================================ */
#pragma once
#include "settings_store.h"

namespace ota {

void begin(const Settings &s);
void loop();

// Senha exigida pelo OTA, pela regra acima.
const String &password();

}  // namespace ota
