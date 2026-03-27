# TuyaLocalConnect

Solução .NET 9 para leitura de sensores Tuya de temperatura e umidade diretamente via protocolo LAN (55AA), **sem depender da nuvem Tuya**. As leituras são publicadas via MQTT, permitindo integrá-las ao Home Assistant como sensores customizados.

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

| Modelo | Protocolo | DPs lidos |
|--------|-----------|-----------|
| TH-01 (WiFi T&H Sensor) | 3.3 | Temperatura, Umidade, Estado bateria, % bateria |
| RMW002 / GRC0002 (LCD) | 3.4 | Temperatura, Umidade, Bateria |

## Payload MQTT

Tópico: `sensor/tuya/th/{deviceId}` (publicado com `retain=true`)

```json
{
  "Temperature": 23.4,
  "Humidity": 58.0,
  "BatteryState": "high",
  "BatteryPercent": 100,
  "TempUnit": "c",
  "HeatIndex": 23.1,
  "ComfortLevel": "Confortável",
  "MeasuredAt": "2025-01-15T10:30:00+03:00"
}
```

## Integração com Home Assistant

Com o MQTT integrado ao Home Assistant (via [MQTT Integration](https://www.home-assistant.io/integrations/mqtt/)), adicione sensores customizados no `configuration.yaml`:

```yaml
mqtt:
  sensor:
    - name: "Sensor T&H - Temperatura"
      unique_id: tuya_th_eb169bae_temperature
      state_topic: "sensor/tuya/th/eb169bae589e44c6b3fieg"
      value_template: "{{ value_json.Temperature }}"
      unit_of_measurement: "°C"
      device_class: temperature
      state_class: measurement

    - name: "Sensor T&H - Umidade"
      unique_id: tuya_th_eb169bae_humidity
      state_topic: "sensor/tuya/th/eb169bae589e44c6b3fieg"
      value_template: "{{ value_json.Humidity }}"
      unit_of_measurement: "%"
      device_class: humidity
      state_class: measurement

    - name: "Sensor T&H - Índice de Calor"
      unique_id: tuya_th_eb169bae_heatindex
      state_topic: "sensor/tuya/th/eb169bae589e44c6b3fieg"
      value_template: "{{ value_json.HeatIndex }}"
      unit_of_measurement: "°C"
      device_class: temperature
      state_class: measurement

    - name: "Sensor T&H - Bateria"
      unique_id: tuya_th_eb169bae_battery
      state_topic: "sensor/tuya/th/eb169bae589e44c6b3fieg"
      value_template: "{{ value_json.BatteryPercent }}"
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
    "BrokerPort": 1883
  },
  "TuyaApi": {
    "LocalBindIp": "0.0.0.0",
    "Devices": [
      {
        "DeviceId": "SEU_DEVICE_ID",
        "DeviceName": "Sala",
        "DeviceModel": "RMW002",
        "LocalKey": "SUA_LOCAL_KEY",
        "DeviceIp": "192.168.1.50"
      }
    ]
  }
}
```

Para obter o `DeviceId` e `LocalKey` de cada sensor, utilize o aplicativo [tinytuya](https://github.com/jasonacox/tinytuya) ou o [Tuya IoT Platform](https://iot.tuya.com/).

Dados sensíveis podem ser mantidos fora do `appsettings.json` via `dotnet user-secrets` (desenvolvimento) ou variáveis de ambiente (produção/Docker).

## Execução

**Via .NET CLI:**
```bash
dotnet run --project MqttThSensorWorker
```

**Via Docker** (build context a partir da raiz da solução - ainda estou validando isso... ):
```bash
docker build -f MqttThSensorWorker/Dockerfile -t tuya-local-connect .

# Usar rede do host para acesso à LAN (necessário para UDP/TCP com os sensores)
docker run --network host tuya-local-connect
```

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
