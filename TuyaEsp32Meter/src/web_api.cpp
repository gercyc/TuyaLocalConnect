#include "web_api.h"

#include <WebServer.h>

#include "device_profiles.h"
#include "web_page.h"
#include "wifi_manager.h"

namespace web_api {
namespace {

constexpr uint32_t kRestartDelayMs = 800;   // tempo para a resposta sair
constexpr const char *kJson = "application/json";
constexpr const char *kCsrfHeader = "X-Requested-With";

WebServer server(80);
Settings settings;
DeviceStore *store = nullptr;
DevicePoller *poller = nullptr;
MqttPublisher *mqtt = nullptr;
uint32_t restartAt = 0;

bool authorized() {
  if (settings.adminPass.isEmpty()) return true;
  if (server.authenticate("admin", settings.adminPass.c_str())) return true;
  server.requestAuthentication(BASIC_AUTH, "TuyaMeter");
  return false;
}

// Rotas que alteram estado exigem um header que o navegador so manda
// cross-origin apos um preflight CORS — que este servidor nunca aprova.
// Sem isso, qualquer site aberto na LAN poderia postar um form
// text/plain para /api/factory-reset, com o Basic Auth em cache junto.
bool authorizedWrite() {
  if (!authorized()) return false;
  if (server.header(kCsrfHeader) == "1") return true;
  server.send(403, kJson, R"({"ok":false,"error":"header X-Requested-With ausente"})");
  return false;
}

void sendJson(int code, const JsonDocument &doc) {
  String out;
  serializeJson(doc, out);
  server.send(code, kJson, out);
}

void sendError(int code, const String &msg) {
  JsonDocument doc;
  doc["ok"] = false;
  doc["error"] = msg;
  sendJson(code, doc);
}

void sendOk() {
  JsonDocument doc;
  doc["ok"] = true;
  sendJson(200, doc);
}

bool readBody(JsonDocument &doc) {
  if (!server.hasArg("plain") || deserializeJson(doc, server.arg("plain")) ||
      !doc.is<JsonObject>()) {
    sendError(400, "corpo JSON invalido");
    return false;
  }
  return true;
}

void scheduleRestart() { restartAt = millis() + kRestartDelayMs; }

/* ---------------- status ---------------- */

void profilesToJson(JsonArray out) {
  size_t count = 0;
  const DeviceProfile *const *profiles = allProfiles(count);
  for (size_t i = 0; i < count; i++) {
    const DeviceProfile &p = *profiles[i];
    JsonObject o = out.add<JsonObject>();
    o["id"] = p.id;
    o["label"] = p.label;
    o["defaultProtocol"] = String(p.defaultProto / 10) + "." + String(p.defaultProto % 10);
    JsonArray ents = o["entities"].to<JsonArray>();
    for (size_t j = 0; j < p.entityCount; j++) {
      const DpEntity &e = p.entities[j];
      JsonObject eo = ents.add<JsonObject>();
      eo["code"] = e.code;
      eo["name"] = e.name;
      eo["dp"] = e.dp;
      if (e.unit) eo["unit"] = e.unit;
      eo["group"] = String(e.group);
    }
  }
}

void handleStatus() {
  if (!authorized()) return;
  JsonDocument doc;
  doc["fw"] = FW_VERSION;
  doc["uptimeSec"] = millis() / 1000;
  doc["freeHeap"] = ESP.getFreeHeap();
  doc["pollSeconds"] = settings.pollSeconds;
  wifi_manager::toJson(doc["wifi"].to<JsonObject>());
  mqtt->toJson(doc["mqtt"].to<JsonObject>());
  profilesToJson(doc["profiles"].to<JsonArray>());

  const std::vector<DeviceStatus> statuses = poller->snapshot();
  const uint32_t now = millis();
  JsonArray devs = doc["devices"].to<JsonArray>();
  for (const DeviceConfig &cfg : store->list()) {
    JsonObject o = devs.add<JsonObject>();
    DeviceStore::toJson(cfg, o);
    JsonObject st = o["status"].to<JsonObject>();
    for (const DeviceStatus &s : statuses) {
      if (s.id != cfg.id) continue;
      st["online"] = s.online;
      st["failures"] = s.failures;
      st["lastError"] = s.lastError;
      st["durationMs"] = s.lastDurationMs;
      if (s.lastOkMs) st["lastOkAgoSec"] = (now - s.lastOkMs) / 1000;
      if (s.lastTryMs) st["lastTryAgoSec"] = (now - s.lastTryMs) / 1000;
      JsonDocument state;
      deserializeJson(state, s.stateJson);
      st["state"] = state;
    }
  }
  sendJson(200, doc);
}

/* ---------------- devices ---------------- */

// Body da pagina -> DeviceConfig. Sem chave no body, reaproveita a do
// device em edicao (a pagina nunca recebe a chave para reenviar) — mas so
// se o IP for o mesmo: senao o "Testar" mandaria trafego cifrado com a
// chave salva para qualquer endereco informado.
bool parseDevice(JsonDocument &body, DeviceConfig &cfg) {
  String err;
  if (!DeviceStore::fromJson(body.as<JsonObjectConst>(), cfg, err)) {
    sendError(400, err);
    return false;
  }
  const String original = body["originalId"] | "";
  if (cfg.localKey.isEmpty() && !original.isEmpty()) {
    DeviceConfig saved;
    if (store->find(original, saved)) {
      if (saved.ip != cfg.ip) {
        sendError(400, "informe a local_key ao trocar o IP");
        return false;
      }
      cfg.localKey = saved.localKey;
    }
  }
  return true;
}

void handleSaveDevice() {
  if (!authorizedWrite()) return;
  JsonDocument body;
  if (!readBody(body)) return;
  DeviceConfig cfg;
  if (!parseDevice(body, cfg)) return;

  // Device novo (sem originalId) com id existente sobrescreveria o outro.
  DeviceConfig existing;
  const String originalId = body["originalId"] | "";
  if (originalId != cfg.id && store->find(cfg.id, existing)) {
    sendError(409, "ja existe um device com este id");
    return;
  }

  String err;
  if (!store->upsert(cfg, err)) { sendError(400, err); return; }

  // Renomear o id equivale a remover o antigo: o MQTT apaga as entidades.
  const String original = body["originalId"] | "";
  if (!original.isEmpty() && original != cfg.id) store->remove(original);

  poller->pollSoon(cfg.id);
  Serial.printf("[web] device salvo: %s (%s)\n", cfg.id.c_str(), cfg.ip.c_str());
  sendOk();
}

void handleDeleteDevice() {
  if (!authorizedWrite()) return;
  const String id = server.arg("id");
  if (!store->remove(id)) { sendError(404, "device nao encontrado"); return; }
  Serial.printf("[web] device removido: %s\n", id.c_str());
  sendOk();
}

void handleTestDevice() {
  if (!authorizedWrite()) return;
  JsonDocument body;
  if (!readBody(body)) return;
  DeviceConfig cfg;
  if (!parseDevice(body, cfg)) return;
  if (cfg.localKey.length() != 16) { sendError(400, "informe a local_key (16 caracteres)"); return; }

  const uint32_t start = millis();
  String raw, err;
  const bool ok = poller->probe(cfg, raw, err);

  JsonDocument doc;
  doc["ok"] = ok;
  doc["durationMs"] = millis() - start;
  if (!ok) {
    doc["error"] = err;
    sendJson(200, doc);
    return;
  }
  JsonDocument dps;
  deserializeJson(dps, raw);
  doc["dps"] = dps;
  if (const DeviceProfile *p = findProfile(cfg.profile))
    applyProfile(*p, dps.as<JsonObjectConst>(), doc["state"].to<JsonObject>());
  sendJson(200, doc);
}

/* ---------------- settings ---------------- */

void handleGetSettings() {
  if (!authorized()) return;
  JsonDocument doc;
  settings::toJson(settings, doc.to<JsonObject>());
  sendJson(200, doc);
}

void handleSaveSettings() {
  if (!authorizedWrite()) return;
  JsonDocument body;
  if (!readBody(body)) return;
  Settings next;
  String err;
  if (!settings::fromJson(body.as<JsonObjectConst>(), settings, next, err)) {
    sendError(400, err);
    return;
  }
  if (!settings::save(next)) { sendError(500, "falha ao gravar na NVS"); return; }
  Serial.println("[web] settings salvos; reiniciando");
  sendOk();
  scheduleRestart();
}

void handleRestart() {
  if (!authorizedWrite()) return;
  sendOk();
  scheduleRestart();
}

void handleFactoryReset() {
  if (!authorizedWrite()) return;
  settings::factoryReset();
  Serial.println("[web] NVS apagada; reiniciando");
  sendOk();
  scheduleRestart();
}

void handleRoot() {
  if (!authorized()) return;
  server.send_P(200, "text/html; charset=utf-8", kWebPage);
}

}  // namespace

void begin(const Settings &s, DeviceStore &st, DevicePoller &p, MqttPublisher &m) {
  settings = s;
  store = &st;
  poller = &p;
  mqtt = &m;

  static const char *collected[] = {kCsrfHeader};
  server.collectHeaders(collected, 1);
  server.on("/", HTTP_GET, handleRoot);
  server.on("/api/status", HTTP_GET, handleStatus);
  server.on("/api/devices", HTTP_POST, handleSaveDevice);
  server.on("/api/devices", HTTP_DELETE, handleDeleteDevice);
  server.on("/api/devices/test", HTTP_POST, handleTestDevice);
  server.on("/api/settings", HTTP_GET, handleGetSettings);
  server.on("/api/settings", HTTP_POST, handleSaveSettings);
  server.on("/api/restart", HTTP_POST, handleRestart);
  server.on("/api/factory-reset", HTTP_POST, handleFactoryReset);
  server.onNotFound([] { server.send(404, "text/plain", "nao encontrado"); });
  server.begin();
}

void loop() {
  server.handleClient();
  if (restartAt != 0 && int32_t(millis() - restartAt) >= 0) ESP.restart();
}

}  // namespace web_api
