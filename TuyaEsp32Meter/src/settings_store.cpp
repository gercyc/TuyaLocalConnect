#include "settings_store.h"

#include <Preferences.h>
#include <esp_random.h>

#if __has_include("secrets.h")
#include "secrets.h"
#endif
#ifndef SEED_WIFI_SSID
#define SEED_WIFI_SSID ""
#define SEED_WIFI_PASS ""
#endif
#ifndef SEED_MQTT_HOST
#define SEED_MQTT_HOST ""
#define SEED_MQTT_PORT 1883
#endif

namespace settings {
namespace {

constexpr const char *kNs = "settings";

bool isValidTopicSegment(const String &s) {
  if (s.isEmpty() || s.length() > 32) return false;
  for (char c : s)
    if (!(isalnum(c) || c == '_' || c == '-')) return false;
  return true;
}

// Senha: campo ausente ou vazio mantem a atual, ja que a pagina nunca a
// recebe de volta. Para apagar, a pagina manda a flag `<campo>Clear`.
String pickSecret(JsonObjectConst in, const char *field, const char *clearFlag,
                  const String &current) {
  if (in[clearFlag] | false) return "";
  const char *v = in[field] | "";
  return strlen(v) > 0 ? String(v) : current;
}

String randomApPass() {
  // Sem 0/O/1/l/I: a senha e lida na serial e digitada no celular.
  static const char kAlphabet[] = "abcdefghjkmnpqrstuvwxyz23456789";
  String out;
  for (int i = 0; i < 10; i++) out += kAlphabet[esp_random() % (sizeof(kAlphabet) - 1)];
  return out;
}

}  // namespace

Settings load() {
  Preferences p;
  p.begin(kNs, true);
  Settings s;
  const bool seeded = p.isKey("seeded");
  s.wifiSsid        = p.getString("wifiSsid", seeded ? "" : SEED_WIFI_SSID);
  s.wifiPass        = p.getString("wifiPass", seeded ? "" : SEED_WIFI_PASS);
  s.mqttHost        = p.getString("mqttHost", seeded ? "" : SEED_MQTT_HOST);
  s.mqttPort        = p.getUShort("mqttPort", SEED_MQTT_PORT);
  s.mqttUser        = p.getString("mqttUser", "");
  s.mqttPass        = p.getString("mqttPass", "");
  s.baseTopic       = p.getString("baseTopic", s.baseTopic);
  s.discoveryPrefix = p.getString("discPrefix", s.discoveryPrefix);
  s.pollSeconds     = p.getUShort("pollSec", s.pollSeconds);
  s.adminPass       = p.getString("adminPass", "");
  s.apPass          = p.getString("apPass", "");
  p.end();

  // Senha do AP aleatoria por placa: uma fixa estaria no repositorio
  // publico, e qualquer queda do roteador abriria o AP para a vizinhanca.
  if (s.apPass.isEmpty()) {
    s.apPass = randomApPass();
    Preferences w;
    w.begin(kNs, false);
    w.putString("apPass", s.apPass);
    w.end();
  }
  return s;
}

bool save(const Settings &s) {
  Preferences p;
  if (!p.begin(kNs, false)) return false;
  p.putBool("seeded", true);
  p.putString("wifiSsid", s.wifiSsid);
  p.putString("wifiPass", s.wifiPass);
  p.putString("mqttHost", s.mqttHost);
  p.putUShort("mqttPort", s.mqttPort);
  p.putString("mqttUser", s.mqttUser);
  p.putString("mqttPass", s.mqttPass);
  p.putString("baseTopic", s.baseTopic);
  p.putString("discPrefix", s.discoveryPrefix);
  p.putUShort("pollSec", s.pollSeconds);
  p.putString("adminPass", s.adminPass);
  p.putString("apPass", s.apPass);
  p.end();
  return true;
}

void factoryReset() {
  Preferences p;
  p.begin(kNs, false);
  p.clear();
  p.end();
  p.begin("devices", false);
  p.clear();
  p.end();
}

void toJson(const Settings &s, JsonObject out) {
  out["wifiSsid"]        = s.wifiSsid;
  out["hasWifiPass"]     = !s.wifiPass.isEmpty();
  out["mqttHost"]        = s.mqttHost;
  out["mqttPort"]        = s.mqttPort;
  out["mqttUser"]        = s.mqttUser;
  out["hasMqttPass"]     = !s.mqttPass.isEmpty();
  out["baseTopic"]       = s.baseTopic;
  out["discoveryPrefix"] = s.discoveryPrefix;
  out["pollSeconds"]     = s.pollSeconds;
  out["hasAdminPass"]    = !s.adminPass.isEmpty();
  // Em claro de proposito: quem ja esta na pagina e admin, e precisa
  // anotar esta senha para o dia em que o roteador sumir.
  out["apPass"]          = s.apPass;
}

bool fromJson(JsonObjectConst in, const Settings &base, Settings &out, String &err) {
  Settings s = base;
  s.wifiSsid        = in["wifiSsid"] | base.wifiSsid.c_str();
  s.mqttHost        = in["mqttHost"] | base.mqttHost.c_str();
  s.mqttUser        = in["mqttUser"] | base.mqttUser.c_str();
  s.baseTopic       = in["baseTopic"] | base.baseTopic.c_str();
  s.discoveryPrefix = in["discoveryPrefix"] | base.discoveryPrefix.c_str();
  s.wifiPass  = pickSecret(in, "wifiPass", "wifiPassClear", base.wifiPass);
  s.mqttPass  = pickSecret(in, "mqttPass", "mqttPassClear", base.mqttPass);
  s.adminPass = pickSecret(in, "adminPass", "adminPassClear", base.adminPass);

  const long port = in["mqttPort"] | long(base.mqttPort);
  const long poll = in["pollSeconds"] | long(base.pollSeconds);
  s.wifiSsid.trim();
  s.mqttHost.trim();

  if (s.wifiSsid.length() > 32)            err = "SSID com mais de 32 caracteres";
  else if (s.wifiPass.length() > 63)       err = "senha do Wi-Fi com mais de 63 caracteres";
  else if (port < 1 || port > 65535)       err = "porta MQTT invalida";
  else if (poll < kMinPollSeconds || poll > kMaxPollSeconds)
    err = "intervalo deve ficar entre " + String(kMinPollSeconds) + " e " +
          String(kMaxPollSeconds) + " s";
  else if (!isValidTopicSegment(s.baseTopic))
    err = "topico base: use letras, numeros, _ ou - (ate 32)";
  else if (!isValidTopicSegment(s.discoveryPrefix))
    err = "prefixo de discovery: use letras, numeros, _ ou - (ate 32)";
  if (!err.isEmpty()) return false;

  s.mqttPort = uint16_t(port);
  s.pollSeconds = uint16_t(poll);
  out = s;
  return true;
}

}  // namespace settings
