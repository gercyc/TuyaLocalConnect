#include "device_profiles.h"

namespace {

constexpr const char *kMeasurement = "measurement";
constexpr const char *kTotalInc    = "total_increasing";

// PJ1103C — "WIFI dual meter" (categoria cz), duas pinças A/B.
// Energia em centesimos de kWh, potencia em decimos de W, corrente em mA.
const DpEntity kDualMeter[] = {
  {115, "total_power",          "Total Power",           "power",        "W",   kMeasurement, 0.1f,  1, nullptr,               'T'},
  {112, "voltage_a",            "Voltage",               "voltage",      "V",   kMeasurement, 0.1f,  1, nullptr,               'T'},
  {111, "freq",                 "Frequency",             "frequency",    "Hz",  kMeasurement, 0.01f, 2, nullptr,               'T'},
  {130, "forward_energy_total", "Total Energy",          "energy",       "kWh", kTotalInc,    0.01f, 2, nullptr,               'T'},
  {131, "reverse_energy_total", "Total Energy Returned", "energy",       "kWh", kTotalInc,    0.01f, 2, nullptr,               'T'},

  {101, "power_a",              "Power A",               "power",        "W",   kMeasurement, 0.1f,  1, nullptr,               'A'},
  {113, "current_a",            "Current A",             "current",      "A",   kMeasurement, 0.001f,3, nullptr,               'A'},
  {110, "power_factor",         "Power Factor A",        "power_factor", nullptr, kMeasurement, 0.01f, 2, nullptr,             'A'},
  {106, "energy_forword_a",     "Energy A",              "energy",       "kWh", kTotalInc,    0.01f, 2, nullptr,               'A'},
  {107, "energy_reverse_a",     "Energy Returned A",     "energy",       "kWh", kTotalInc,    0.01f, 2, nullptr,               'A'},
  {102, "direction_a",          "Direction A",           nullptr,        nullptr, nullptr,    0.0f,  0, "mdi:swap-horizontal", 'A'},

  {105, "power_b",              "Power B",               "power",        "W",   kMeasurement, 0.1f,  1, nullptr,               'B'},
  {114, "current_b",            "Current B",             "current",      "A",   kMeasurement, 0.001f,3, nullptr,               'B'},
  {121, "power_factor_b",       "Power Factor B",        "power_factor", nullptr, kMeasurement, 0.01f, 2, nullptr,             'B'},
  {108, "energy_forword_b",     "Energy B",              "energy",       "kWh", kTotalInc,    0.01f, 2, nullptr,               'B'},
  {109, "energy_reserse_b",     "Energy Returned B",     "energy",       "kWh", kTotalInc,    0.01f, 2, nullptr,               'B'},
  {104, "direction_b",          "Direction B",           nullptr,        nullptr, nullptr,    0.0f,  0, "mdi:swap-horizontal", 'B'},
};

const DeviceProfile kDualMeterProfile = {
  "pj1103c", "Medidor dual Wi-Fi (PJ1103C)", "Tuya", "PJ1103C",
  kDualMeter, sizeof(kDualMeter) / sizeof(kDualMeter[0]), 35,
};

const DeviceProfile *const kProfiles[] = {&kDualMeterProfile};

}  // namespace

const DeviceProfile *findProfile(const String &id) {
  for (const DeviceProfile *p : kProfiles)
    if (id == p->id) return p;
  return nullptr;
}

const DeviceProfile *const *allProfiles(size_t &count) {
  count = sizeof(kProfiles) / sizeof(kProfiles[0]);
  return kProfiles;
}

void applyProfile(const DeviceProfile &profile, JsonObjectConst dps, JsonObject state) {
  for (size_t i = 0; i < profile.entityCount; i++) {
    const DpEntity &e = profile.entities[i];
    JsonVariantConst raw = dps[String(e.dp)];
    if (raw.isNull()) continue;

    if (e.scale > 0.0f && (raw.is<long>() || raw.is<double>())) {
      // Texto com casas fixas: evita 114.80000000000001 no payload.
      state[e.code] = serialized(String(raw.as<double>() * e.scale, (unsigned int)e.decimals));
    } else {
      state[e.code] = raw;
    }
  }
}
