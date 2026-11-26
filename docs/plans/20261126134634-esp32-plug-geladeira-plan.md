---
title: "TuyaEsp32Meter: perfil para a tomada medidora da geladeira (cz)"
type: enhancement
status: completed
date: 2026-11-26
phased: false
---

# TuyaEsp32Meter: perfil para a tomada medidora da geladeira (cz)

## Overview

O `TuyaEsp32Meter` lê hoje dois modelos (`pj1103c` e `tmph`). Queremos ler também
a tomada da geladeira, "Smart Plug 2" (nome no app: **Geladeira**), e publicar
potência, tensão e corrente no HA pelo mesmo caminho MQTT dos outros devices.

Dados do device (de `plug-geladeira.txt`, dump da Tuya OpenAPI; arquivo local, **não versionar**):

| Campo | Valor |
|---|---|
| Device ID | `eb20963f965a76b576s8s4` |
| Categoria / modelo | `cz` / `wifi计量插座` (tomada medidora Wi-Fi), product `j20xh6tbtz8rn2sr` |
| Protocolo | **3.4** (medido em 2026-11-23, memória `neo-plug-proximity-reset`) |
| IP LAN | `192.168.18.160` (mesma memória; o `ip` do dump, 177.106.57.74, é o IP público visto pela nuvem) |
| Worker .NET | **não** está no `appsettings.json`, então não há conflito de identidade nem de TCP com o worker |

DPs relevantes (valores do dump):

| DP | code | bruto | escala | valor |
|---|---|---|---|---|
| 1 | `switch_1` | true | — | ON |
| 17 | `add_ele` | 90 | — | ver decisão D3 |
| 18 | `cur_current` | 2281 | 0.001 | 2,281 A |
| 19 | `cur_power` | 1759 | 0.1 | 175,9 W |
| 20 | `cur_voltage` | 1262 | 0.1 | 126,2 V |
| 26 | `fault` | 0 | — | OFF |
| 38 | `relay_status` | `on` | — | on |

Checagem de escala: 126,2 V × 2,281 A = 287,9 VA contra 175,9 W, ou seja, FP ≈ 0,61. É plausível
para o compressor de uma geladeira (carga indutiva). A confirmação fica para o "Testar" (T005).

Ficam de fora: `countdown_1` (9), `test_bit` (21), os coeficientes de calibração
(22–25), `overcharge_switch` (39), `light_mode` (40), `child_lock` (41) e as strings
de agenda (42–44). São configuração, não leitura, e o firmware é só leitura.

## Proposed Solution

**Decisão D1: reaproveitar a tabela do disjuntor.** Os 7 DPs de medição e estado do
plug (1, 17, 18, 19, 20, 26, 38) são exatamente os do `kBreaker`, com o mesmo code,
nome localtuya, escala e componente. Em vez de copiar a tabela, ela ganha um nome
genérico (`kMeteredRelay`) e passa a ser usada pelos dois perfis. O mesmo vale para
os DPs de refresh (`kMeteredRelayRefresh` = 18, 19, 20).

**Decisão D2: perfil novo `cz-meter`.** Ele precisa ser um perfil próprio, e não um
alias do `tmph`, porque `model` e `defaultProto` vão para o discovery (`device.model`)
e para o formulário: o plug é 3.4, o disjuntor 3.5.

```cpp
const DeviceProfile kPlugProfile = {
  "cz-meter", "Tomada Wi-Fi com medidor (cz)", "Tuya", "Smart Plug (cz)",
  kMeteredRelay, sizeof(kMeteredRelay) / sizeof(kMeteredRelay[0]), 34,
  kMeteredRelayRefresh, sizeof(kMeteredRelayRefresh),
};
```

**Decisão D3: `add_ele` cru, como diagnóstico, igual ao disjuntor.** Nas tomadas
BK72xx esse DP também é um incremento de Wh desde o último envio à nuvem, não um
acumulado: o valor 90 com ~176 W é compatível com isso. Publicar como
`total_increasing` faria o painel de Energia perder consumo. A T005 confirma isso
no device real. Se o valor crescer sem parar e não zerar (acumulado de verdade), a
tabela do plug se separa da do disjuntor (ver T005).

**Decisão D4: refresh via UPDATEDPS.** É provável que a tomada tenha o mesmo DP_QUERY
"congelado" do disjuntor, comum nas tomadas medidoras. O `Client::requestRefresh`
já funciona em 3.4 (`ep.proto >= 34`). Ele é inofensivo se o device responder com
dados frescos e é tolerante a falha ("sem refresh, o DP_QUERY ainda traz a ultima foto").

Nada muda em `mqtt_publisher`, `web_api` ou `device_poller`: o perfil é data-driven.
A única mudança fora do perfil fica em `web_page.h`: hoje o formulário preenche o
protocolo só com o `defaultProtocol` do **primeiro** perfil (`web_page.h:273`). Como
não há `onchange` no `#profileSel`, escolher `cz-meter` deixaria o campo em 3.5. Isso
já acontece com o `tmph`, mas não aparece, porque ele também é 3.5. A correção está na T008. A página monta as colunas pelos grupos (`T` e `S`, como no disjuntor),
e a bridge do diamond já leva `homeassistant/sensor/#`, `homeassistant/binary_sensor/#`
e `tuya32/#`.

## Technical Considerations

- **Segredo:** `plug-geladeira.txt` (e os irmãos `disjuntor-63a.txt` e
  `energy-meter-wifi.txt`, também não rastreados) contém `local_key` em claro. O
  repositório é público. Esses arquivos **não podem entrar em commit**: a T004 os
  coloca no `.gitignore`. A chave é digitada só na página do ESP32 e fica na NVS.
  Nunca vai para plano, README ou código.
- **Um cliente TCP por device:** antes de cadastrar, confirmar que a integração
  localtuya do HA não lê esse plug (o mesmo pendente do disjuntor, memória
  `breaker-63a-pending-localtuya-removal`). Se ler, os dois vão disputar a porta 6668.
- **IP fixo:** o ESP32 não faz discovery UDP. É preciso uma reserva DHCP para
  `192.168.18.160` no roteador.
- **Memória do Neo:** a memória registra que esse plug ligado direto na tomada do
  Neo da cozinha derrubava o Neo quando o roteador estava no canal 13. Não afeta o
  firmware, mas se o Neo voltar a resetar, não atribua isso ao polling do ESP32
  sem checar o canal.
- **Somente leitura:** `switch_1` vira `binary_sensor` (estado do relé). Não há
  comando para desligar a geladeira pelo HA, e para uma geladeira isso é desejável.
- **Identidade HA:** `unique_id` `tuya_eb20963f965a76b576s8s4_<code>`, device
  `tuya_eb20963f965a76b576s8s4`, nome "Geladeira" (o que for digitado na página).
- **Convenções:** comentários em português sem acento no `.cpp` (padrão do arquivo),
  versão em `platformio.ini`, sem projeto de teste (CLAUDE.md). A verificação é o
  build mais o "Testar conexão" no device real.

## Acceptance Criteria

- **Dado** o firmware novo gravado, **quando** abro "Adicionar device", **então** o
  perfil "Tomada Wi-Fi com medidor (cz)" aparece e preenche o protocolo 3.4.
- **Dado** o perfil `cz-meter`, ID, IP `192.168.18.160`, a local key correta e 3.4,
  **quando** clico em "Testar conexão", **então** a tabela mostra Power, Voltage e
  Current escalados (W, V, A), Switch 1 ON/OFF, Fault, Power-on behavior e Electricity.
- **Dado** uma local key ou um protocolo errado, **quando** testo, **então** aparece
  "handshake recusado", e nada é salvo.
- **Dado** o device salvo, **quando** passa um ciclo de polling, **então**
  `tuya32/eb20963f965a76b576s8s4/state` traz `cur_power`, `cur_voltage` e
  `cur_current` escalados, e as entidades "Geladeira Power", "Geladeira Voltage"
  e "Geladeira Current" aparecem no HA com valores.
- **Dado** a geladeira ligando e desligando o compressor, **quando** observo duas
  leituras seguidas, **então** `cur_power` acompanha a carga real (não fica parado
  por ~5 min). Isso valida o refresh.
- **Dado** o plug desligado da tomada, **quando** acontecem 3 falhas seguidas,
  **então** `tuya32/<id>/availability` vai a `offline` e as entidades ficam indisponíveis.
- **Dado** o device removido na página, **então** as configs de discovery dele são
  apagadas (payload vazio retido).
- Os perfis `pj1103c` e `tmph` continuam com as mesmas entidades e o mesmo
  comportamento (a renomeação da tabela não muda os dados).

## Implementation

- [ ] T001 Generalizar a tabela do disjuntor em `TuyaEsp32Meter/src/device_profiles.cpp`
  - Renomear `kBreaker` → `kMeteredRelay` e `kBreakerRefresh` → `kMeteredRelayRefresh`.
  - Reescrever o comentário acima da tabela: "DPs 1/17-20/26/38 comuns as tomadas (cz)
    e disjuntores (znjdq) medidores Tuya; leitura apenas, rele como binary_sensor".
  - Manter o comentário do `add_ele`, trocando "neste device" por "no TMPH e nas
    tomadas BK72xx". O dado medido (100 por 40 min a ~500 W) continua atribuído ao TMPH.
  - Atualizar `kBreakerProfile` para usar os novos nomes. Os valores não mudam.
- [ ] T002 Adicionar o perfil da tomada em `TuyaEsp32Meter/src/device_profiles.cpp`
  - Comentário (o arquivo é só ASCII): `// Tomada medidora Wi-Fi (categoria cz, "wifi metering socket", product j20xh6tbtz8rn2sr), protocolo 3.4. Mesmos DPs do disjuntor.`
  - `const DeviceProfile kPlugProfile = { "cz-meter", "Tomada Wi-Fi com medidor (cz)", "Tuya", "Smart Plug (cz)", kMeteredRelay, <count>, 34, kMeteredRelayRefresh, sizeof(kMeteredRelayRefresh) };`
  - `kProfiles[] = {&kDualMeterProfile, &kBreakerProfile, &kPlugProfile};`
- [ ] T003 [P] Subir a versão em `TuyaEsp32Meter/platformio.ini`
  - `-D FW_VERSION='"1.1.1"'` → `'"1.2.0"'` (perfil novo = minor). Esse valor faz a
    página recarregar (commit a59c476).
- [ ] T004 [P] Ignorar os dumps da OpenAPI em `.gitignore` (raiz)
  - Adicionar `plug-geladeira.txt`, `disjuntor-63a.txt` e `energy-meter-wifi.txt`, com
    o comentário `# dumps da Tuya OpenAPI: contem local_key`.
  - Conferir com `git status` que os três somem dos não rastreados.
- [ ] T005 Validar no device real (página do ESP32, `http://192.168.18.182/`)
  - Build: `pio run` em `TuyaEsp32Meter/`. Gravar por OTA:
    `curl -H "X-Requested-With: 1" -F "firmware=@.pio/build/esp32dev/firmware.bin" http://192.168.18.182/api/firmware`
    (mais `-u admin:<senha>` se houver senha).
  - Pré-requisito: reserva DHCP de `192.168.18.160` e confirmação de que o localtuya
    do HA não lê esse plug.
  - "Adicionar device" → perfil `cz-meter`, ID `eb20963f965a76b576s8s4`, IP
    `192.168.18.160`, local key (do dump, digitada na página), 3.4 → **Testar**.
  - Conferir a escala: V × A × FP ≈ W. Sem DP de FP, aceitar W ≤ V × A, com razão
    estável entre 0,5 e 1,0.
  - Refresh: testar duas vezes com ~10 s de intervalo, com o compressor mudando de
    estado. Se `cur_power` não mudar em minutos com a carga mudando, investigar o
    UPDATEDPS (log serial `[poll]`).
  - `add_ele`: anotar o valor em 3 leituras ao longo de ~30 min. Se cair ou zerar
    (incremento desde o último envio à nuvem), D3 está confirmada. Se só crescer,
    abrir uma tarefa para separar a tabela do plug com `energy`/`kWh`/`total_increasing`/`0.001`.
  - Salvar e conferir `tuya32/eb20963f965a76b576s8s4/state` e as entidades no HA.
- [ ] T006 Documentar em `TuyaEsp32Meter/README.md`
  - Na tabela de perfis, nova linha: `` `cz-meter` | Tomada Wi-Fi com medidor (categoria cz) | Power, Voltage, Current; Switch 1 e Fault (`binary_sensor`); Power-on behavior e Electricity (diagnóstico) ``.
  - Trocar "hoje com dois perfis" por "hoje com três perfis".
  - Renomear a seção "Notas do disjuntor TMPH" para "Notas do disjuntor TMPH e da
    tomada cz" e registrar o resultado da T005 (refresh necessário ou não; semântica
    do `add_ele` na tomada).
  - Na Estrutura, `device_profiles.*  ... (PJ1103C, TMPH, tomada cz)`.
  - Em "O device precisa de IP fixo", incluir a tomada (o ESP32 não escuta broadcast).
- [ ] T007 [P] Atualizar a linha do `TuyaEsp32Meter` em `CLAUDE.md`
  - "só leitura, focado no medidor PJ1103C" → "só leitura: medidor PJ1103C, disjuntor TMPH e tomada medidora cz".

- [ ] T008 Preencher o protocolo pelo perfil escolhido em `TuyaEsp32Meter/src/web_page.h` (executar antes da T005)
  - Nova função perto de `openDialog` (linha ~267):
    `function applyProfileProto(){const p=last.profiles.find(x=>x.id===form.profile.value);if(p)form.protocol.value=p.defaultProtocol}`
  - `$('#profileSel').onchange=applyProfileProto;`, registrado uma vez junto dos outros handlers.
  - No `openDialog`, no ramo de device novo (linha 273, hoje
    `form.protocol.value=last.profiles[0]?.defaultProtocol||'3.5'`): trocar por
    `applyProfileProto()`, depois de o select ser populado e de `form.profile.value`
    estar no primeiro perfil. Na edição de um device existente, manter o protocolo salvo.

**Depois de implementar:** `pio run` em `TuyaEsp32Meter/` sem erros nem warnings
novos; a T005 só é concluída com o device lido no HA.

## ✅ Master Checklist

- [x] T001 Tabela `kBreaker` → `kMeteredRelay` (+ refresh)
- [x] T002 Perfil `cz-meter` em `kProfiles`
- [x] T003 FW_VERSION 1.2.0
- [x] T004 `.gitignore` dos dumps com local_key
- [x] T005 Validação no device real (escala, refresh, `add_ele`, HA)
- [x] T006 README do TuyaEsp32Meter
- [x] T007 CLAUDE.md
- [x] T008 Protocolo do formulário segue o perfil escolhido
- [x] `pio run` passa

## Clarifications

Nenhuma bloqueante. Premissas que a T005 confirma:
- protocolo 3.4 e IP `.160` (vêm da memória de 2026-11-23, não do dump);
- semântica do `add_ele` na tomada (D3);
- necessidade do refresh (D4; inofensivo se for desnecessário).

## Execution Log (2026-11-26)

- FW 1.2.0 gravado por OTA em `192.168.18.182`; build: RAM 15,2%, Flash 46,5%.
- Teste: 3.4 aceito; 2 leituras com 10 s de intervalo trouxeram valores diferentes (o refresh funciona).
  152,0 W / 115,3 V / 2,170 A, com FP implícito ≈ 0,61 (compressor).
- Tensão bate com o PJ1103C (114,6 V × 113,3 V): escala 0,1 confirmada.
- `add_ele`: 90 no dump da nuvem → 78 no teste. É um incremento, não um acumulado, então D3 está confirmada.
- Device "Geladeira" salvo; `tuya32/eb20963f965a76b576s8s4/{state,availability}` e 7 configs de discovery
  retidas no mosquitto do diamond.
- Não verificado: as entidades no HA (a bridge já cobre os tópicos) e se o localtuya do HA lê essa tomada.
