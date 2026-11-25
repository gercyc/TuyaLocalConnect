/* ============================================================
 *  device_profiles.h — mapa DP -> entidade do Home Assistant
 *
 *  O firmware nao conhece devices, so perfis: o device cadastrado na
 *  pagina escolhe um perfil, e o perfil diz quais DPs publicar, com que
 *  escala e como aparecem no HA. Um modelo novo e um perfil novo aqui.
 *
 *  Nomes, classes e escalas seguem o HaEntityCatalog do worker .NET
 *  (que por sua vez copia o localtuya).
 * ============================================================ */
#pragma once
#include <Arduino.h>
#include <ArduinoJson.h>

struct DpEntity {
  uint8_t     dp;
  const char *code;          // chave no JSON de estado
  const char *name;          // nome da entidade no HA
  const char *deviceClass;   // nullptr = sem device_class
  const char *unit;
  const char *stateClass;
  float       scale;         // valor publicado = bruto * scale; 0 = texto
  uint8_t     decimals;
  const char *icon;
  char        group;         // 'T' = destaque; demais = colunas na pagina
  // Campos opcionais: omitidos no inicializador ficam nullptr.
  const char *component;      // nullptr = "sensor"; ou "binary_sensor"
  const char *entityCategory; // nullptr ou "diagnostic"
};

inline const char *entityComponent(const DpEntity &e) {
  return e.component ? e.component : "sensor";
}

struct DeviceProfile {
  const char     *id;
  const char     *label;
  const char     *manufacturer;
  const char     *model;
  const DpEntity *entities;
  size_t          entityCount;
  uint8_t         defaultProto;
  // DPs que o device so atualiza em degraus no DP_QUERY: pedidos via
  // UPDATEDPS antes de cada leitura. nullptr/0 = sem refresh.
  const uint8_t  *refreshDps;
  size_t          refreshCount;
};

// nullptr se o id nao existir.
const DeviceProfile *findProfile(const String &id);

const DeviceProfile *const *allProfiles(size_t &count);

// Converte os DPs crus do device nos valores do perfil (escalados, por
// `code`) e grava em `state`. DPs ausentes nao mexem no que ja havia:
// assim uma resposta parcial nao apaga a ultima leitura conhecida.
void applyProfile(const DeviceProfile &profile, JsonObjectConst dps, JsonObject state);
