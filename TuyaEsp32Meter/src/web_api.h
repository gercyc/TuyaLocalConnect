/* ============================================================
 *  web_api.h — pagina de configuracao e API REST
 *
 *    GET    /                    pagina (self-contained, funciona no AP)
 *    GET    /api/status          Wi-Fi, MQTT, devices com ultima leitura
 *    POST   /api/devices         cria/edita device (JSON)
 *    DELETE /api/devices?id=     remove device
 *    POST   /api/devices/test    consulta o device sem salvar
 *    GET    /api/settings        settings (senhas mascaradas)
 *    POST   /api/settings        grava settings e reinicia
 *    POST   /api/restart         reinicia
 *    POST   /api/factory-reset   apaga NVS e reinicia
 *
 *  Com senha de admin configurada, tudo exige HTTP Basic (usuario
 *  "admin"). A local_key nunca sai pela API.
 * ============================================================ */
#pragma once
#include "device_poller.h"
#include "device_store.h"
#include "mqtt_publisher.h"
#include "settings_store.h"

namespace web_api {

void begin(const Settings &s, DeviceStore &store, DevicePoller &poller,
           MqttPublisher &mqtt);
void loop();

}  // namespace web_api
