#include "device_store.h"

#include <Preferences.h>

#include <algorithm>

#include "device_profiles.h"
#include "lock_guard.h"

namespace {

constexpr const char *kNs  = "devices";
constexpr const char *kKey = "list";
constexpr size_t kKeyLen   = 16;
constexpr size_t kMaxIdLen = 32;
constexpr size_t kMaxName  = 48;

bool isValidId(const String &id) {
  if (id.isEmpty() || id.length() > kMaxIdLen) return false;
  for (char c : id)
    if (!isalnum(c)) return false;
  return true;
}

bool isValidIp(const String &ip) {
  IPAddress addr;
  return addr.fromString(ip) && ip.indexOf('.') > 0;
}

bool isValidProto(uint8_t p) { return p == 33 || p == 34 || p == 35; }

}  // namespace

DeviceStore::DeviceStore() : mutex_(xSemaphoreCreateMutex()) {}

void DeviceStore::begin() {
  LockGuard lock(mutex_);
  loadLocked();
}

void DeviceStore::loadLocked() {
  Preferences p;
  p.begin(kNs, true);
  const String raw = p.getString(kKey, "[]");
  p.end();

  JsonDocument doc;
  devices_.clear();
  if (deserializeJson(doc, raw)) {
    Serial.println("[store] lista de devices corrompida na NVS; ignorando");
    return;
  }
  for (JsonObjectConst o : doc.as<JsonArrayConst>()) {
    DeviceConfig cfg;
    cfg.id       = o["id"] | "";
    cfg.name     = o["name"] | "";
    cfg.ip       = o["ip"] | "";
    cfg.localKey = o["key"] | "";
    cfg.profile  = o["profile"] | "";
    cfg.proto    = o["proto"] | 35;
    cfg.enabled  = o["enabled"] | true;
    String err;
    if (validate(cfg, true, err)) devices_.push_back(cfg);
    else Serial.printf("[store] device '%s' ignorado: %s\n", cfg.id.c_str(), err.c_str());
  }
}

bool DeviceStore::saveLocked() const {
  JsonDocument doc;
  JsonArray arr = doc.to<JsonArray>();
  for (const DeviceConfig &d : devices_) {
    JsonObject o = arr.add<JsonObject>();
    o["id"] = d.id;
    o["name"] = d.name;
    o["ip"] = d.ip;
    o["key"] = d.localKey;
    o["profile"] = d.profile;
    o["proto"] = d.proto;
    o["enabled"] = d.enabled;
  }
  String raw;
  serializeJson(doc, raw);

  Preferences p;
  if (!p.begin(kNs, false)) return false;
  const bool ok = p.putString(kKey, raw) == raw.length();
  p.end();
  return ok;
}

std::vector<DeviceConfig> DeviceStore::list() const {
  LockGuard lock(mutex_);
  return devices_;
}

bool DeviceStore::find(const String &id, DeviceConfig &out) const {
  LockGuard lock(mutex_);
  for (const DeviceConfig &d : devices_)
    if (d.id == id) { out = d; return true; }
  return false;
}

bool DeviceStore::upsert(DeviceConfig cfg, String &err) {
  LockGuard lock(mutex_);
  auto it = std::find_if(devices_.begin(), devices_.end(),
                         [&](const DeviceConfig &d) { return d.id == cfg.id; });
  const bool exists = it != devices_.end();
  if (exists && cfg.localKey.isEmpty()) cfg.localKey = it->localKey;

  if (!validate(cfg, true, err)) return false;
  if (!exists && devices_.size() >= kMaxDevices) {
    err = "limite de " + String(kMaxDevices) + " devices atingido";
    return false;
  }

  const std::vector<DeviceConfig> previous = devices_;
  if (exists) *it = cfg;
  else devices_.push_back(cfg);

  if (!saveLocked()) {
    devices_ = previous;
    err = "falha ao gravar na NVS";
    return false;
  }
  return true;
}

bool DeviceStore::remove(const String &id) {
  LockGuard lock(mutex_);
  const std::vector<DeviceConfig> previous = devices_;
  devices_.erase(std::remove_if(devices_.begin(), devices_.end(),
                                [&](const DeviceConfig &d) { return d.id == id; }),
                 devices_.end());
  if (devices_.size() == previous.size()) return false;
  if (!saveLocked()) { devices_ = previous; return false; }
  return true;
}

bool DeviceStore::fromJson(JsonObjectConst in, DeviceConfig &out, String &err) {
  DeviceConfig cfg;
  cfg.id       = in["id"] | "";
  cfg.name     = in["name"] | "";
  cfg.ip       = in["ip"] | "";
  cfg.localKey = in["localKey"] | "";
  cfg.profile  = in["profile"] | "";
  cfg.enabled  = in["enabled"] | true;

  // A pagina manda "3.5"; aceita tambem 35 numerico. Qualquer outra coisa
  // vira 0 e cai no padrao do perfil — nada de conversao que trunque.
  const String protoText = in["protocol"] | "";
  const long protoNum = in["protocol"] | 0L;
  if (protoText == "3.3" || protoNum == 33) cfg.proto = 33;
  else if (protoText == "3.4" || protoNum == 34) cfg.proto = 34;
  else if (protoText == "3.5" || protoNum == 35) cfg.proto = 35;
  else if (!protoText.isEmpty() || protoNum != 0) {
    err = "protocolo deve ser 3.3, 3.4 ou 3.5";
    return false;
  } else cfg.proto = 0;

  cfg.id.trim();
  cfg.name.trim();
  cfg.ip.trim();
  if (cfg.proto == 0) {
    const DeviceProfile *prof = findProfile(cfg.profile);
    cfg.proto = prof ? prof->defaultProto : 35;
  }
  if (!validate(cfg, false, err)) return false;
  out = cfg;
  return true;
}

bool DeviceStore::validate(const DeviceConfig &cfg, bool requireKey, String &err) {
  if (!isValidId(cfg.id))               err = "id invalido (so letras e numeros, ate 32)";
  else if (cfg.name.isEmpty() || cfg.name.length() > kMaxName)
    err = "nome obrigatorio (ate " + String(kMaxName) + " caracteres)";
  else if (!isValidIp(cfg.ip))          err = "IP invalido";
  else if (!isValidProto(cfg.proto))    err = "protocolo deve ser 3.3, 3.4 ou 3.5";
  else if (!findProfile(cfg.profile))   err = "perfil desconhecido";
  else if (!cfg.localKey.isEmpty() && cfg.localKey.length() != kKeyLen)
    err = "local_key deve ter exatamente 16 caracteres";
  else if (requireKey && cfg.localKey.isEmpty()) err = "local_key obrigatoria";
  return err.isEmpty();
}

void DeviceStore::toJson(const DeviceConfig &cfg, JsonObject out) {
  out["id"]       = cfg.id;
  out["name"]     = cfg.name;
  out["ip"]       = cfg.ip;
  out["profile"]  = cfg.profile;
  out["protocol"] = String(cfg.proto / 10) + "." + String(cfg.proto % 10);
  out["enabled"]  = cfg.enabled;
  out["hasKey"]   = !cfg.localKey.isEmpty();
}
