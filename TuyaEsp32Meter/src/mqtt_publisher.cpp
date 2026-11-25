#include "mqtt_publisher.h"

#include <algorithm>

#include "device_profiles.h"

namespace {

constexpr uint32_t kRetryMs      = 5000;
constexpr uint32_t kSyncEveryMs  = 500;
constexpr uint16_t kSocketTimeoutSec = 3;  // connect/CONNACK sem resposta nao trava o loop()
constexpr uint16_t kBufferSize   = 1024;   // discovery com bloco device passa de 512B
constexpr uint16_t kKeepAliveSec = 30;     // folga para o "Testar" da pagina, que bloqueia
constexpr const char *kOnline    = "online";
constexpr const char *kOffline   = "offline";
// Mesma identidade do HaDiscoveryPublisher do worker .NET (unique_id
// tuya_<devId>_<code>, device tuya_<devId>): o device que migra do worker
// para o ESP32 continua sendo o mesmo para o HA. O outro lado disso e que
// worker e ESP32 nao podem publicar o mesmo device ao mesmo tempo.
constexpr const char *kHaIdPrefix = "tuya_";
// expire_after do worker (ExpireAfterSeconds). Nunca abaixo de 3 leituras,
// senao um intervalo de polling longo marcaria o sensor como indisponivel.
constexpr uint32_t kExpireAfterSec = 300;
constexpr uint32_t kExpireMinPolls = 3;

}  // namespace

MqttPublisher::MqttPublisher(DeviceStore &store, DevicePoller &poller)
    : store_(store), poller_(poller), mqtt_(net_) {}

void MqttPublisher::begin(const Settings &s) {
  settings_ = s;
  const String mac = WiFi.macAddress();
  clientId_ = "tuya32-" + mac.substring(12);
  clientId_.replace(":", "");
  bridgeAvailTopic_ = s.baseTopic + "/bridge/availability";

  mqtt_.setBufferSize(kBufferSize);
  mqtt_.setKeepAlive(kKeepAliveSec);
  mqtt_.setSocketTimeout(kSocketTimeoutSec);
  net_.setTimeout(kSocketTimeoutSec);
  mqtt_.setServer(settings_.mqttHost.c_str(), settings_.mqttPort);
  mqtt_.setCallback([this](char *t, uint8_t *p, unsigned int l) { onMessage(t, p, l); });
}

String MqttPublisher::haId(const String &id) const { return String(kHaIdPrefix) + id; }

String MqttPublisher::discoveryTopic(const String &id, const DpEntity &e) const {
  return settings_.discoveryPrefix + "/" + entityComponent(e) + "/" + id + "/" + e.code + "/config";
}

// Mesmos templates do worker: ausente vira None (unknown); no binario,
// qualquer valor verdadeiro (inclusive bitmap de falha != 0) vira ON.
static String valueTemplate(const DpEntity &e) {
  const String v = String("value_json.get('") + e.code + "')";
  if (strcmp(entityComponent(e), "binary_sensor") != 0) return "{{ " + v + " }}";
  return "{{ 'None' if " + v + " is none else ('ON' if " + v + " else 'OFF') }}";
}

String MqttPublisher::deviceTopic(const String &id, const char *leaf) const {
  return settings_.baseTopic + "/" + id + "/" + leaf;
}

bool MqttPublisher::pub(const String &topic, const String &payload, bool retain) {
  const bool ok = mqtt_.publish(topic.c_str(), payload.c_str(), retain);
  ok ? publishedOk_++ : publishedFail_++;
  return ok;
}

bool MqttPublisher::connect() {
  const bool hasUser = !settings_.mqttUser.isEmpty();
  const bool ok = mqtt_.connect(clientId_.c_str(),
                                hasUser ? settings_.mqttUser.c_str() : nullptr,
                                hasUser ? settings_.mqttPass.c_str() : nullptr,
                                bridgeAvailTopic_.c_str(), 0, true, kOffline);
  lastState_ = mqtt_.state();
  if (!ok) {
    Serial.printf("[mqtt] conexao com %s:%u falhou (state %d)\n",
                  settings_.mqttHost.c_str(), settings_.mqttPort, lastState_);
    return false;
  }
  connects_++;
  Serial.printf("[mqtt] conectado a %s:%u\n", settings_.mqttHost.c_str(), settings_.mqttPort);
  pub(bridgeAvailTopic_, kOnline);
  mqtt_.subscribe((settings_.discoveryPrefix + "/status").c_str());

  // Sessao nova: o broker pode ter perdido os retained, entao tudo e
  // republicado. Remocoes pendentes continuam valendo (mantem profile).
  for (auto &kv : published_) {
    kv.second.signature = "";
    kv.second.version = 0;
    kv.second.online = -1;
  }
  return true;
}

void MqttPublisher::onMessage(char *topic, uint8_t *payload, unsigned int len) {
  // HA reiniciou: republica discovery e estado, como no connect.
  if (String(topic) != settings_.discoveryPrefix + "/status") return;
  if (len == strlen(kOnline) && memcmp(payload, kOnline, len) == 0) {
    Serial.println("[mqtt] Home Assistant online; republicando discovery");
    for (auto &kv : published_) {
      kv.second.signature = "";
      kv.second.version = 0;
      kv.second.online = -1;
    }
  }
}

void MqttPublisher::loop() {
  if (settings_.mqttHost.isEmpty() || WiFi.status() != WL_CONNECTED) return;

  if (!mqtt_.connected()) {
    const uint32_t now = millis();
    if (lastAttemptMs_ != 0 && now - lastAttemptMs_ < kRetryMs) return;
    lastAttemptMs_ = now;
    if (!connect()) return;
  }
  mqtt_.loop();

  // Sincronizar copia a lista de devices e o estado de todos: a cada
  // volta do loop() (~2ms) isso so fragmentaria o heap sem ganho.
  const uint32_t now = millis();
  if (now - lastSyncMs_ < kSyncEveryMs) return;
  lastSyncMs_ = now;

  const std::vector<DeviceConfig> devices = store_.list();
  syncDiscovery(devices);
  publishStates(devices);
}

void MqttPublisher::syncDiscovery(const std::vector<DeviceConfig> &devices) {
  // Removidos: apaga as entidades no HA.
  for (auto it = published_.begin(); it != published_.end();) {
    const String &id = it->first;
    const bool exists = std::any_of(devices.begin(), devices.end(),
                                    [&](const DeviceConfig &d) { return d.id == id; });
    if (exists) { ++it; continue; }
    // So esquece o device se a remocao saiu inteira; senao tenta de novo.
    it = removeDiscovery(id, it->second.profile) ? published_.erase(it) : std::next(it);
  }

  for (const DeviceConfig &cfg : devices) {
    const DeviceProfile *profile = findProfile(cfg.profile);
    if (!profile) continue;
    Published &p = published_[cfg.id];
    const String signature = cfg.name + "|" + cfg.profile;
    if (p.signature == signature) continue;

    // Trocou de perfil: os codes antigos podem nao existir no novo.
    if (!p.profile.isEmpty() && p.profile != cfg.profile &&
        !removeDiscovery(cfg.id, p.profile))
      continue;
    p.profile = cfg.profile;
    // Assinatura so e gravada com tudo publicado: falha parcial (link caiu,
    // payload maior que o buffer) e refeita na proxima sincronizacao.
    if (publishDiscovery(cfg, *profile)) p.signature = signature;
  }
}

bool MqttPublisher::publishDiscovery(const DeviceConfig &cfg, const DeviceProfile &profile) {
  bool allOk = true;
  const String ha = haId(cfg.id);
  const String stateTopic = deviceTopic(cfg.id, "state");
  const String availTopic = deviceTopic(cfg.id, "availability");
  const uint32_t expireAfter =
      std::max(kExpireAfterSec, uint32_t(settings_.pollSeconds) * kExpireMinPolls);

  for (size_t i = 0; i < profile.entityCount; i++) {
    const DpEntity &e = profile.entities[i];
    JsonDocument doc;
    doc["name"] = e.name;
    doc["unique_id"] = ha + "_" + e.code;
    // Nome da entidade relativo ao device: "WIFI dual meter Total Power".
    doc["has_entity_name"] = true;
    doc["state_topic"] = stateTopic;
    doc["value_template"] = valueTemplate(e);
    doc["expire_after"] = expireAfter;
    if (e.entityCategory) doc["entity_category"] = e.entityCategory;
    if (e.deviceClass) doc["device_class"] = e.deviceClass;
    if (e.unit) doc["unit_of_measurement"] = e.unit;
    if (e.stateClass) doc["state_class"] = e.stateClass;
    if (e.icon) doc["icon"] = e.icon;

    JsonArray avail = doc["availability"].to<JsonArray>();
    avail.add<JsonObject>()["topic"] = bridgeAvailTopic_;
    avail.add<JsonObject>()["topic"] = availTopic;
    doc["availability_mode"] = "all";

    JsonObject dev = doc["device"].to<JsonObject>();
    dev["identifiers"].to<JsonArray>().add(ha);
    dev["name"] = cfg.name;
    dev["model"] = profile.model;
    dev["manufacturer"] = profile.manufacturer;

    JsonObject origin = doc["origin"].to<JsonObject>();
    origin["name"] = "TuyaEsp32Meter";
    origin["sw_version"] = FW_VERSION;

    String payload;
    serializeJson(doc, payload);
    allOk &= pub(discoveryTopic(cfg.id, e), payload);
  }
  Serial.printf("[mqtt] discovery %s: %s (%u entidades)\n", allOk ? "publicado" : "FALHOU",
                cfg.id.c_str(), (unsigned)profile.entityCount);
  return allOk;
}

bool MqttPublisher::removeDiscovery(const String &id, const String &profileId) {
  const DeviceProfile *profile = findProfile(profileId);
  if (!profile) return true;   // nada conhecido para apagar
  bool allOk = true;
  for (size_t i = 0; i < profile->entityCount; i++)
    allOk &= pub(discoveryTopic(id, profile->entities[i]), "");
  allOk &= pub(deviceTopic(id, "state"), "");
  allOk &= pub(deviceTopic(id, "availability"), "");
  Serial.printf("[mqtt] discovery %s: %s\n", allOk ? "removido" : "remocao FALHOU", id.c_str());
  return allOk;
}

void MqttPublisher::publishStates(const std::vector<DeviceConfig> &devices) {
  const std::vector<DeviceStatus> statuses = poller_.snapshot();
  for (const DeviceConfig &cfg : devices) {
    auto pit = published_.find(cfg.id);
    if (pit == published_.end()) continue;
    Published &p = pit->second;

    const auto sit = std::find_if(statuses.begin(), statuses.end(),
                                  [&](const DeviceStatus &s) { return s.id == cfg.id; });
    const bool hasStatus = sit != statuses.end();
    const bool online = cfg.enabled && hasStatus && sit->online;

    if (hasStatus && sit->version != p.version && sit->version > 0) {
      if (pub(deviceTopic(cfg.id, "state"), sit->stateJson)) p.version = sit->version;
    }
    if (p.online != int8_t(online)) {
      if (pub(deviceTopic(cfg.id, "availability"), online ? kOnline : kOffline))
        p.online = int8_t(online);
    }
  }
}

void MqttPublisher::toJson(JsonObject out) const {
  out["host"] = settings_.mqttHost;
  out["port"] = settings_.mqttPort;
  out["connected"] = const_cast<PubSubClient &>(mqtt_).connected();
  out["state"] = lastState_;
  out["connects"] = connects_;
  out["published"] = publishedOk_;
  out["failed"] = publishedFail_;
  out["baseTopic"] = settings_.baseTopic;
}
