#include "device_poller.h"

#include <WiFi.h>

#include <algorithm>

#include "device_profiles.h"
#include "lock_guard.h"

namespace {

constexpr uint32_t kTaskStack   = 8192;
constexpr UBaseType_t kTaskPrio = 1;
constexpr BaseType_t kTaskCore  = 0;   // loop() roda no core 1
constexpr uint32_t kTickMs      = 250;
constexpr uint32_t kProbeWaitMs = 2000;

tuya::Endpoint toEndpoint(const DeviceConfig &cfg) {
  tuya::Endpoint ep;
  ep.ip = cfg.ip;
  ep.id = cfg.id;
  ep.proto = cfg.proto;
  memcpy(ep.localKey, cfg.localKey.c_str(), tuya::kKeyLen);
  return ep;
}

}  // namespace

DevicePoller::DevicePoller(DeviceStore &store)
    : store_(store),
      stateMutex_(xSemaphoreCreateMutex()),
      queryMutex_(xSemaphoreCreateMutex()) {}

void DevicePoller::begin(uint16_t pollSeconds) {
  setInterval(pollSeconds);
  xTaskCreatePinnedToCore(taskEntry, "tuya-poll", kTaskStack, this, kTaskPrio,
                          nullptr, kTaskCore);
}

void DevicePoller::setInterval(uint16_t pollSeconds) {
  intervalMs_ = uint32_t(pollSeconds) * 1000UL;
}

void DevicePoller::taskEntry(void *arg) { static_cast<DevicePoller *>(arg)->run(); }

void DevicePoller::run() {
  for (;;) {
    vTaskDelay(pdMS_TO_TICKS(kTickMs));
    const std::vector<DeviceConfig> devices = store_.list();
    pruneRemoved(devices);
    if (WiFi.status() != WL_CONNECTED) continue;

    const uint32_t now = millis();
    for (const DeviceConfig &cfg : devices) {
      if (!cfg.enabled) continue;

      bool due;
      {
        LockGuard lock(stateMutex_);
        DeviceStatus &st = status_[cfg.id];
        st.id = cfg.id;
        due = pollSoon_[cfg.id] || st.lastTryMs == 0 ||
              now - st.lastTryMs >= intervalMs_;
        pollSoon_[cfg.id] = false;
      }
      if (due) pollOne(cfg);
    }
  }
}

void DevicePoller::pruneRemoved(const std::vector<DeviceConfig> &devices) {
  LockGuard lock(stateMutex_);
  for (auto it = status_.begin(); it != status_.end();) {
    const bool exists = std::any_of(devices.begin(), devices.end(),
                                    [&](const DeviceConfig &d) { return d.id == it->first; });
    if (!exists) pollSoon_.erase(it->first);
    it = exists ? std::next(it) : status_.erase(it);
  }
}

bool DevicePoller::query(const DeviceConfig &cfg, JsonDocument &dps, String &err) {
  LockGuard lock(queryMutex_);
  return client_.queryDps(toEndpoint(cfg), dps, err);
}

void DevicePoller::pollOne(const DeviceConfig &cfg) {
  const DeviceProfile *profile = findProfile(cfg.profile);
  const uint32_t start = millis();
  JsonDocument dps;
  String err;
  const bool ok = query(cfg, dps, err);
  const uint32_t elapsed = millis() - start;

  LockGuard lock(stateMutex_);
  DeviceStatus &st = status_[cfg.id];
  st.id = cfg.id;
  st.lastTryMs = millis();
  st.lastDurationMs = elapsed;

  if (!ok || !profile) {
    st.failures++;
    st.lastError = profile ? err : "perfil desconhecido";
    if (st.failures >= kOfflineAfter) st.online = false;
    Serial.printf("[poll] %s falhou (%u seguidas): %s\n", cfg.id.c_str(),
                  st.failures, st.lastError.c_str());
    return;
  }

  JsonDocument state;
  deserializeJson(state, st.stateJson);
  if (!state.is<JsonObject>()) state.to<JsonObject>();
  applyProfile(*profile, dps.as<JsonObjectConst>(), state.as<JsonObject>());

  String json;
  serializeJson(state, json);
  st.stateJson = json;
  st.failures = 0;
  st.online = true;
  st.lastError = "";
  st.lastOkMs = st.lastTryMs;
  st.version++;
}

std::vector<DeviceStatus> DevicePoller::snapshot() const {
  LockGuard lock(stateMutex_);
  std::vector<DeviceStatus> out;
  out.reserve(status_.size());
  for (const auto &kv : status_) out.push_back(kv.second);
  return out;
}

void DevicePoller::pollSoon(const String &id) {
  LockGuard lock(stateMutex_);
  pollSoon_[id] = true;
}

bool DevicePoller::probe(const DeviceConfig &cfg, String &rawDpsJson, String &err) {
  // Chamado do loop(): esperar a leitura periodica terminar e ainda rodar
  // a propria consulta passaria do keepalive do MQTT. Se o poller estiver
  // ocupado, desiste logo e a pagina pede para tentar de novo.
  if (xSemaphoreTake(queryMutex_, pdMS_TO_TICKS(kProbeWaitMs)) != pdTRUE) {
    err = "leitura periodica em andamento; tente de novo em alguns segundos";
    return false;
  }
  JsonDocument dps;
  const bool ok = client_.queryDps(toEndpoint(cfg), dps, err);
  xSemaphoreGive(queryMutex_);
  if (ok) serializeJson(dps, rawDpsJson);
  return ok;
}
