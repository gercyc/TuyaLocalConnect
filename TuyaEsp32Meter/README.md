# TuyaEsp32Meter

Firmware ESP32 que lê devices Tuya direto pela LAN (TCP 6668, protocolos
3.3/3.4/3.5) e publica as leituras no MQTT com discovery do Home Assistant.
É a versão embarcada do `MqttThSensorWorker`, hoje com dois perfis:

| Perfil | Device | Entidades |
|---|---|---|
| `pj1103c` | Medidor de energia dual Wi-Fi (PJ1103C) | potência, corrente, FP, energia e direção por canal; tensão, frequência, totais |
| `tmph` | Disjuntor Wi-Fi 63A com medidor (TMPH) | Power, Voltage, Current; Switch 1 e Fault (`binary_sensor`); Power-on behavior e Electricity (diagnóstico) |

Os devices são cadastrados **pela página web** e ficam gravados na NVS do
ESP32. Adicionar, editar ou remover um device não exige recompilar.

## Gravar

```bash
cp include/secrets.example.h include/secrets.h   # Wi-Fi/MQTT do 1º boot (gitignored)
pio run -t upload
pio device monitor
```

Testado num ESP32-D0WDQ6 (4 MB, `esp32dev`). A serial mostra o IP; abra-o no
navegador.

### Gravar pela rede (OTA)

Depois da primeira gravação por USB, há dois caminhos pela rede:

- **Pela página** (Configurações → Firmware): envie o
  `.pio/build/esp32dev/firmware.bin`. Ou por curl:
  `curl -H "X-Requested-With: 1" -F "firmware=@.pio/build/esp32dev/firmware.bin" http://<ip>/api/firmware`
  (mais `-u admin:<senha>` se a página tiver senha).
- **Pelo PlatformIO** (`espota`): `pio run -e esp32dev-ota -t upload`, com a
  senha em `$env:TUYAMETER_OTA_PASS`. No Windows, o espota precisa que o
  firewall aceite a conexão **de volta** do ESP32. Sem uma regra liberando
  isso, ele autentica e falha com "No response from device". O upload pela
  página não tem esse problema.

A senha do OTA é a senha da página; se a página não tiver senha, vale a
senha do AP de resgate. O firmware é validado (magic e tamanho) antes de
trocar de partição; um `.bin` inválido é recusado e o atual segue rodando.

Sem `secrets.h`, ou se o Wi-Fi não conectar em 20 s, o ESP32 abre o AP
**`TuyaMeter-XXXX`**. A página fica em `http://192.168.4.1/`. Depois que o STA
conecta e o AP fica 30 s sem clientes, o AP é desligado.

A senha do AP é **aleatória por placa**, gerada no primeiro boot. Ela aparece
na serial a cada boot (`[wifi] AP de resgate: ... senha ...`) e em
Configurações na página. Anote-a: uma senha fixa estaria neste repositório
público, e qualquer queda do roteador abriria o AP para quem estivesse perto.

## Cadastrar um device

1. **Adicionar device** → nome, perfil, Device ID, IP, local key e protocolo.
2. **Testar conexão** consulta o device sem salvar e mostra os valores já
   escalados. Chave ou protocolo errados aparecem aqui como "handshake
   recusado".
3. **Salvar**: a primeira leitura sai em instantes, e as entidades aparecem
   no HA.

A local key nunca volta pela API: ao editar, deixe o campo vazio para manter
a atual. Remover um device apaga as entidades dele no HA (configs de
discovery vazias e retidas).

O device precisa de IP fixo (reserva DHCP): o PJ1103C e o TMPH não anunciam
por broadcast UDP, então não há como redescobrir o endereço.

### Notas do disjuntor TMPH

- **Medição congelada no DP_QUERY:** ele devolve no DP_QUERY uma medição de
  até ~5 min atrás. O perfil pede `UPDATEDPS` (cmd `0x12`) para os DPs 18–20
  antes de cada leitura, e os valores frescos chegam em frames STATUS.
- **`add_ele` não é energia acumulada:** é um contador de Wh desde o último
  envio à nuvem, e satura em 100; ficou em 100 por 40 min com ~500 W. Vai
  cru, como diagnóstico. Para kWh no painel de Energia, crie no HA o helper
  **Integral (Riemann)** sobre o `Power`.

## MQTT

| Tópico | Conteúdo |
|---|---|
| `tuya32/bridge/availability` | `online`/`offline` do ESP32 (Last Will) |
| `tuya32/<devId>/availability` | `offline` após 3 falhas seguidas de leitura |
| `tuya32/<devId>/state` | JSON por código de DP, valores escalados, retain |
| `homeassistant/{sensor,binary_sensor}/<devId>/<code>/config` | discovery, retain |

O ESP32 assina `homeassistant/status` e republica o discovery quando o HA
reinicia.

O discovery tem a **mesma identidade do worker .NET**:
- tópico de discovery, `unique_id` `tuya_<devId>_<code>` e device
  `tuya_<devId>`;
- `has_entity_name`, `value_template` com `.get()` e `expire_after` 300 s (no
  mínimo 3 leituras).

Assim, um device que migra do worker para o ESP32 continua sendo o mesmo
para o HA. Por isso **o mesmo device não pode estar nos dois ao mesmo
tempo**: tire-o do `appsettings.json` do worker antes de cadastrá-lo aqui.

Diferenças em relação ao worker, que não afetam a identidade:
- o estado vai em `tuya32/<devId>/state`;
- há availability por device;
- o `origin` é `TuyaEsp32Meter`.

**Broker:** o ESP32 publica no mosquitto do diamond (`192.168.18.137`), que
tem uma bridge para o broker do homeserver (`192.168.101.3`), só alcançável
via NetBird. Manifesto em
`homelab/manifests/diamond-server/mosquito-broker/1-config-map.yml`. Um
tópico novo precisa de uma linha `topic` lá.

## API

Todas as rotas exigem HTTP Basic (usuário `admin`) se houver senha da página.
As rotas de escrita exigem também o header `X-Requested-With: 1` (proteção
contra CSRF: um site qualquer aberto na LAN não consegue enviá-lo sem
preflight). Exemplo: `curl -H 'X-Requested-With: 1' -X POST http://<ip>/api/restart`.

| Método | Rota | |
|---|---|---|
| GET | `/api/status` | Wi-Fi, MQTT, devices e última leitura |
| POST | `/api/devices` | cria/edita (`originalId` para renomear o id) |
| DELETE | `/api/devices?id=` | remove |
| POST | `/api/devices/test` | consulta sem salvar; devolve DPs crus e escalados |
| GET/POST | `/api/settings` | settings (senhas mascaradas); POST grava e reinicia |
| POST | `/api/restart`, `/api/factory-reset` | |
| POST | `/api/firmware` | upload multipart do `firmware.bin` (OTA) |

## Estrutura

```
src/
  main.cpp              setup/loop
  tuya/                 protocolo: crypto (mbedTLS), frames 55AA/6699, cliente TCP
  device_profiles.*     perfil -> DPs, escala, entidades HA e refresh (PJ1103C, TMPH)
  ota.*                 ArduinoOTA (espota); o upload pela página fica em web_api.cpp
  device_store.*        devices na NVS (mutex; leitura devolve cópia)
  settings_store.*      Wi-Fi/MQTT/intervalo/senha na NVS
  device_poller.*       task no core 0 que consulta os devices
  mqtt_publisher.*      discovery + estado (só no loop: PubSubClient não é thread-safe)
  wifi_manager.*        STA com AP de resgate
  web_api.* / web_page.h  API REST e página self-contained (funciona sem internet)
```

**Modelo novo** = um `DeviceProfile` novo em `device_profiles.cpp` (DP, code,
escala, device_class, `sensor`/`binary_sensor`, DPs de refresh). Nomes e
escalas seguem o `HaEntityCatalog` do worker (tabelas do localtuya). Antes de
confiar numa escala, compare V × A × FP com a potência numa leitura real do
"Testar".

## Limitações

- Só leitura: não há controle de DPs (o worker .NET faz isso para switches).
- Um cliente TCP por device: se o worker .NET também consultar o mesmo
  device, os dois disputam a conexão e surgem falhas esporádicas.
- A página não tem HTTPS; a senha opcional trafega em Basic Auth na LAN.
