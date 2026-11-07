# TuyaLocalConnect

Solução .NET 9 para leitura de sensores Tuya de temperatura e umidade diretamente via protocolo LAN, **sem depender da nuvem Tuya**. As leituras são publicadas via MQTT, permitindo integrá-las ao Home Assistant como sensores customizados.

Suporta as versões **3.3**, **3.4** e **3.5** do protocolo — esta última usa um envelope de frame próprio (`6699`) com AES-GCM, distinto do `55AA` das anteriores.

## Motivação

Sensores Tuya T&H normalmente requerem conexão com os servidores da Tuya para funcionar com o Home Assistant. Esta solução elimina essa dependência ao se comunicar diretamente com os dispositivos na rede local, resultando em:

- Menor latência nas leituras
- Funcionamento sem internet
- Controle total sobre os dados

## Como funciona

O serviço escuta broadcasts UDP que os dispositivos Tuya emitem periodicamente na rede local (porta 6667). Ao detectar um dispositivo configurado, abre uma conexão TCP (porta 6668), lê os Data Points (DPs) de temperatura, umidade e bateria, e publica o resultado no broker MQTT com `retain=true`.

```
Sensor Tuya  →  UDP broadcast (6667)  →  TuyaLanDiscoveryService
                                                    ↓
                                       TCP query (6668)  →  TuyaLanListenerService
                                                                      ↓
                                                          MQTT: sensor/tuya/th/{deviceId}
```

## Dispositivos suportados

| Modelo | Protocolo | DPs lidos | Status |
|--------|-----------|-----------|--------|
| TH-01 (WiFi T&H Sensor) | 3.3 | Temperatura, Umidade, Estado bateria, % bateria | Ativo |
| RMW002 / GRC0002 (LCD) | 3.4 | Temperatura, Umidade, Bateria | **Desativado** — não está na rede local; mapeamento mantido no código, sem device para validar |

As três versões de protocolo (3.3, 3.4 e 3.5) estão implementadas na camada de transporte e validadas com devices reais. A tabela acima lista os sensores T&H, que têm mapeamento próprio para `SensorReading` em `TuyaProtocolConstants`.

### Plugs, interruptores e outros devices

Qualquer device com o mapa `Dps` configurado (veja [Configuração](#configuração)) é publicado no Home Assistant via **MQTT Discovery**, no padrão de entidades do [localtuya](https://github.com/xZetsubou/hass-localtuya): a entidade é escolhida pelo **código** do DP (`switch_1`, `cur_power`, `add_ele`…), com o mesmo nome, `device_class`, unidade e escala padrão que o localtuya usa nas categorias `kg`, `cz` e `pc`.

| Código | Entidade HA | Escala / unidade |
|--------|-------------|------------------|
| `switch`, `switch_1`…`switch_8` | `switch` controlável (`outlet` em `cz`/`pc`) | — |
| `cur_power` | `sensor` power | ×0.1 → W |
| `cur_voltage` | `sensor` voltage | ×0.1 → V |
| `cur_current` | `sensor` current | mA |
| `add_ele` | `sensor` energy, `total_increasing` | ×0.001 → kWh |
| `countdown_N` | `sensor` duration (diagnóstico) | s |
| `relay_status`, `light_mode` | `sensor` (diagnóstico) | rótulos do localtuya (`Last State`, `Position`…) |
| `child_lock`, `overcharge_switch`, `switch_backlight` | `switch` controlável (configuração) | — |
| `fault` | `binary_sensor` problem (diagnóstico) | — |
| `doorcontact_state`, `battery_percentage` | `binary_sensor` door / `sensor` battery | % |

DPs com códigos fora da tabela (coeficientes de calibração, strings de agendamento) são ignorados, como no localtuya.

Os **switches são controláveis** pelo HA: o comando chega em `tuya/{deviceId}/{código}/set` (`ON`/`OFF`), vira `CONTROL` (3.3) ou `CONTROL_NEW` (3.4/3.5) para o device, e o estado é relido e publicado assim que o device reflete a mudança. O que no localtuya é `select` ou `number` (`relay_status`, `light_mode`, `countdown_N`) ainda é `sensor` somente leitura.

- Discovery: `homeassistant/{switch|binary_sensor|sensor}/{deviceId}/{código}/config` (retain), publicado na primeira leitura de cada execução
- Comando: `tuya/{deviceId}/{código}/set`
- Disponibilidade: `MqttBrokerSettings:AvailabilityTopic` (ex.: `tuya/bridge/availability`) recebe `online` ao conectar e `offline` pelo Last Will se o worker cair; todas as entidades usam esse tópico
- Estado: `tuya/{deviceId}/state` (retain), JSON com chave pelo código e valor já escalado:

```json
{"switch_1":true,"add_ele":0.063,"cur_current":2095,"cur_power":145.1,"cur_voltage":126.2,"relay_status":"Last State","child_lock":false}
```

Sensores e binary_sensors têm `expire_after` (padrão 300 s): sem leitura nesse intervalo, o HA os marca como indisponíveis. O HA não oferece `expire_after` para `switch`, que fica indisponível só pelo tópico de disponibilidade.

> Requer **CamposDev.Mqtt 1.1.0** (retain, subscribe e Last Will). Até essa versão estar publicada no nuget.org, o `nuget.config` da raiz aponta para um feed local em `../Microservices/CamposDev.Microservice/CamposDev.Mqtt/bin/LocalFeed` — o build do Docker só funciona depois da publicação.

## Payload MQTT

Tópico: `sensor/tuya/th/{deviceId}` (publicado com `retain=true`)

```json
{
  "temperature": 23.4,
  "humidity": 58.0,
  "batteryState": "high",
  "batteryPercent": 100,
  "tempUnit": "c",
  "heatIndex": 23.1,
  "comfortLevel": "Confortavel",
  "measuredAt": "2025-01-15T10:30:00+00:00"
}
```

> As chaves são serializadas em **camelCase** e `measuredAt` é sempre UTC. Os valores de `comfortLevel` (`Muito desconfortavel`, `Desconfortavel`, `Confortavel`, `Frio`, `Razoavel`) são emitidos sem acentuação.

## Integração com Home Assistant

Com o MQTT integrado ao Home Assistant (via [MQTT Integration](https://www.home-assistant.io/integrations/mqtt/)), adicione sensores customizados no `configuration.yaml`:

```yaml
mqtt:
  sensor:
    - name: "Sensor T&H - Temperatura"
      unique_id: tuya_th_eb169bae_temperature
      state_topic: "sensor/tuya/th/eb169bae589e44c6b3fieg"
      value_template: "{{ value_json.temperature }}"
      unit_of_measurement: "°C"
      device_class: temperature
      state_class: measurement

    - name: "Sensor T&H - Umidade"
      unique_id: tuya_th_eb169bae_humidity
      state_topic: "sensor/tuya/th/eb169bae589e44c6b3fieg"
      value_template: "{{ value_json.humidity }}"
      unit_of_measurement: "%"
      device_class: humidity
      state_class: measurement

    - name: "Sensor T&H - Índice de Calor"
      unique_id: tuya_th_eb169bae_heatindex
      state_topic: "sensor/tuya/th/eb169bae589e44c6b3fieg"
      value_template: "{{ value_json.heatIndex }}"
      unit_of_measurement: "°C"
      device_class: temperature
      state_class: measurement

    - name: "Sensor T&H - Bateria"
      unique_id: tuya_th_eb169bae_battery
      state_topic: "sensor/tuya/th/eb169bae589e44c6b3fieg"
      value_template: "{{ value_json.batteryPercent }}"
      unit_of_measurement: "%"
      device_class: battery
      state_class: measurement
```

> Substitua `eb169bae589e44c6b3fieg` pelo `DeviceId` do seu dispositivo configurado em `appsettings.json`.

## Configuração

Edite `MqttThSensorWorker/appsettings.json`:

```json
{
  "MqttBrokerSettings": {
    "BrokerHost": "192.168.1.10",
    "BrokerPort": 1883,
    "AvailabilityTopic": "tuya/bridge/availability"
  },
  "TuyaApi": {
    "Devices": [
      {
        "DeviceId": "SEU_DEVICE_ID",
        "DeviceName": "Sala",
        "DeviceModel": "RMW002",
        "DeviceIp": "192.168.1.50",
        "Protocol": "3.4",
        "LocalKey": "SUA_LOCAL_KEY"
      },
      {
        "DeviceId": "SEU_PLUG_ID",
        "DeviceName": "Geladeira",
        "DeviceModel": "Smart plug",
        "DeviceIp": "192.168.1.51",
        "Protocol": "3.4",
        "LocalKey": "SUA_LOCAL_KEY",
        "Category": "cz",
        "Dps": {
          "1":  { "Code": "switch_1", "Name": "Tomada" },
          "18": { "Code": "cur_current" },
          "19": { "Code": "cur_power" },
          "20": { "Code": "cur_voltage", "Scale": 0.1 }
        }
      }
    ],
    "HomeAssistant": {
      "DiscoveryPrefix": "homeassistant",
      "StateTopicPrefix": "tuya",
      "ExpireAfterSeconds": 300
    }
  }
}
```

`Dps` mapeia o número do DP para o código Tuya. `Name` e `Scale` são opcionais e sobrescrevem o padrão do localtuya. Os códigos vêm da API Tuya (`GET /v2.0/cloud/thing/{id}/shadow/properties`, campos `dp_id` e `code`). Device sem `Dps` segue o fluxo de sensor T&H. `Category` (campo `category` da API) é opcional e muda só o ícone das tomadas (`cz`, `pc`).

O campo `Protocol` aceita `"3.3"`, `"3.4"` ou `"3.5"`. É opcional: sem ele, vale a versão anunciada pelo device no broadcast UDP. Configurá-lo explicitamente é preferível, porque alguns devices anunciam uma versão e aceitam outra nas conexões TCP.

Para obter o `DeviceId` e a `LocalKey` de cada sensor, utilize o aplicativo [tinytuya](https://github.com/jasonacox/tinytuya) ou o [Tuya IoT Platform](https://iot.tuya.com/). A versão do protocolo aparece no campo `version` do broadcast UDP.

### LocalKey

As chaves **não ficam no git**. No `appsettings.json` versionado, `LocalKey` fica vazia. Os valores reais vão em `MqttThSensorWorker/appsettings.local.json`, que está no `.gitignore` e segue a mesma ordem do array `Devices`:

```json
{
  "TuyaApi": {
    "Devices": [
      { "DeviceId": "SEU_DEVICE_ID", "LocalKey": "SUA_LOCAL_KEY" },
      { "DeviceId": "SEU_PLUG_ID",   "LocalKey": "SUA_LOCAL_KEY" }
    ]
  }
}
```

O `DeviceId` repetido serve só para conferir que a posição bate. O merge é por índice: device fora de ordem recebe a chave errada.

O arquivo é copiado no `dotnet publish`, então **entra na imagem** gerada a partir de uma máquina que o tenha. Não publique essa imagem em registry público. Device sem chave é ignorado e aparece num aviso no log (`sem LocalKey serão ignorados`).

Variáveis de ambiente e user-secrets sobrescrevem o arquivo local (o índice é a posição do device no array `Devices`):

```bash
# desenvolvimento
dotnet user-secrets --project MqttThSensorWorker set "TuyaApi:Devices:0:LocalKey" "SUA_LOCAL_KEY"

# produção / Docker
TuyaApi__Devices__0__LocalKey=SUA_LOCAL_KEY
```

> Reordenar ou remover devices do array desloca os índices — se estiver usando secrets ou variáveis de ambiente, eles precisam ser reajustados junto.

## Execução

**Via .NET CLI:**
```bash
dotnet run --project MqttThSensorWorker
```

**Via Docker ou Podman** (build context a partir da raiz da solução):
```bash
podman build -f MqttThSensorWorker/Dockerfile -t homeserver:32081/tuya-local-connect:latest .

# Rede do host: obrigatória para receber o broadcast UDP (6667) dos devices
podman run --network host homeserver:32081/tuya-local-connect:latest

podman push homeserver:32081/tuya-local-connect:latest
```

Ou `docker compose up -d`: o `docker-compose.yml` puxa a imagem do registry (o bloco `build` está comentado para quando precisar gerar localmente).

> **Podman no Windows** roda numa VM WSL: `--network host` é a rede da VM, não a do Windows. Funciona com o WSL em `networkingMode=mirrored` (`~/.wslconfig`), em que a VM recebe a interface da LAN. Validado em 07/11/2026: discovery, leitura dos 11 devices e comandos a partir do container.

**Kubernetes (cluster diamond):** manifestos em `C:\source\gercyc\homelab\manifests\diamond-server\tuya-local-connect`, com `hostNetwork` pelo mesmo motivo. Detalhes no README daquele diretório.

**Via Docker ou Podman com variáveis de ambiente:**
```bash
docker run --network host \
  -e TuyaApi__Devices__0__LocalKey="SUA_LOCAL_KEY" \
  -e MqttBrokerSettings__BrokerHost="192.168.1.10" \
  tuya-local-connect
```

## Estrutura da solução

```
TuyaLocalConnect/
├── CamposDev.Tuya/          # Biblioteca: protocolo LAN, discovery, MQTT publish
└── MqttThSensorWorker/      # Worker Service: host, DI, Serilog, Docker
```

## Dependências externas

- Broker MQTT (ex: [Mosquitto](https://mosquitto.org/)) acessível na rede local
- Servidor [Seq](https://datalust.co/seq) opcional para visualização de logs estruturados
