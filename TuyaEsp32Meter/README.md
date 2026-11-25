# TuyaEsp32Meter

Firmware ESP32 que lê devices Tuya direto pela LAN (TCP 6668, protocolos
3.3/3.4/3.5) e publica as leituras no MQTT com discovery do Home Assistant.
É a versão embarcada do `MqttThSensorWorker`, hoje focada no medidor de
energia dual Wi-Fi (PJ1103C).

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

O device precisa de IP fixo (reserva DHCP): o PJ1103C não anuncia por
broadcast UDP, então não há como redescobrir o endereço.

## MQTT

| Tópico | Conteúdo |
|---|---|
| `tuya32/bridge/availability` | `online`/`offline` do ESP32 (Last Will) |
| `tuya32/<devId>/availability` | `offline` após 3 falhas seguidas de leitura |
| `tuya32/<devId>/state` | JSON por código de DP, valores escalados, retain |
| `homeassistant/sensor/tuya32_<devId>/<code>/config` | discovery, retain |

O ESP32 assina `homeassistant/status` e republica o discovery quando o HA
reinicia. O prefixo `tuya32` e o `unique_id` `tuya32_*` não colidem com as
entidades do worker .NET.

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

## Estrutura

```
src/
  main.cpp              setup/loop
  tuya/                 protocolo: crypto (mbedTLS), frames 55AA/6699, cliente TCP
  device_profiles.*     perfil -> DPs, escala e entidades HA (PJ1103C)
  device_store.*        devices na NVS (mutex; leitura devolve cópia)
  settings_store.*      Wi-Fi/MQTT/intervalo/senha na NVS
  device_poller.*       task no core 0 que consulta os devices
  mqtt_publisher.*      discovery + estado (só no loop: PubSubClient não é thread-safe)
  wifi_manager.*        STA com AP de resgate
  web_api.* / web_page.h  API REST e página self-contained (funciona sem internet)
```

**Modelo novo** = um `DeviceProfile` novo em `device_profiles.cpp` (DP, code,
escala, device_class). As escalas seguem o `HaEntityCatalog` do worker.

## Limitações

- Só leitura: não há controle de DPs (o worker .NET faz isso para switches).
- Um cliente TCP por device: se o worker .NET também consultar o mesmo
  device, os dois disputam a conexão e surgem falhas esporádicas.
- A página não tem HTTPS; a senha opcional trafega em Basic Auth na LAN.
