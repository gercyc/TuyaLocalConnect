# CamposDev.Tuya

Biblioteca .NET 9 para descoberta e leitura de dispositivos Tuya via **protocolo LAN 55AA** — sem dependência da nuvem Tuya.

## Funcionalidades

| Serviço | Descrição |
|---------|-----------|
| `TuyaLanDiscoveryService` | Escuta broadcasts UDP na porta 6667 e mantém um inventário de dispositivos Tuya ativos na rede |
| `TuyaLanListenerService` | Ao detectar o wake-up de um device configurado, conecta via TCP (porta 6668) e lê os DPs diretamente. Suporta protocolo **3.3** (CRC32, sem sessão) e **3.4** (HMAC-SHA256, sessão negociada) |
| `TuyaTokenService` | Gerencia tokens de acesso à Tuya OpenAPI (obtenção + renovação via refresh token) |

## Registro no DI

A forma recomendada de registrar todos os serviços é através da extensão `AddTuyaLan`:

```csharp
// Requer que IMqttBrokerService já esteja registrado
builder.Services.AddTuyaLan(builder.Configuration);
```

O método lê a seção `TuyaApi` do `appsettings.json` por padrão. Para usar outra seção:

```csharp
builder.Services.AddTuyaLan(builder.Configuration, configSection: "MeuSensorTuya");
```

> **Pré-requisito:** `IMqttBrokerService` (de `CamposDev.Mqtt`) deve estar registrado antes de chamar `AddTuyaLan`, pois `TuyaLanListenerService` depende dele para publicar as leituras via MQTT.

### Registro manual (granular)

```csharp
// Registrar apenas o token service + discovery (sem listener MQTT)
builder.Services.AddSingleton<TuyaApiOptions>(
    builder.Configuration.GetSection("TuyaApi").Get<TuyaApiOptions>()!);
builder.Services.AddSingleton<TuyaTokenService>();
builder.Services.AddSingleton<TuyaLanDiscoveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TuyaLanDiscoveryService>());
```

## Configuração (`appsettings.json`)

A configuração suporta **múltiplos devices** via array `Devices`. Cada device tem sua própria `LocalKey`, `DeviceId` e demais parâmetros.

```json
"TuyaApi": {
  "Endpoint":     "https://openapi.tuyaus.com",
  "AccessId":     "<seu_access_id>",
  "AccessSecret": "<seu_access_secret>",
  "Devices": [
    {
      "DeviceId":    "eb169bae589e44c6b3fieg",
      "DeviceName":  "T&H Sensor Mobile",
      "DeviceModel": "RMW002",
      "LocalKey":    "myLocalKey12345",
      "DeviceIp":    "192.168.101.109"
    },
    {
      "DeviceId":    "abc123def456",
      "DeviceName":  "T&H Sensor TH-01",
      "DeviceModel": "TH-01",
      "LocalKey":    "myLocalKey12345",
      "DeviceIp":    "192.168.101.110"
    }
  ],
  "LocalBindIp": "192.168.101.149"
}
```

| Campo | Obrigatório | Descrição |
|-------|-------------|-----------|
| `Devices[].DeviceId` | Sim | ID do dispositivo na Tuya (gwId) |
| `Devices[].LocalKey` | Para LAN | Chave AES-128 local do device. Sem ela, o device é ignorado pelo `TuyaLanListenerService` |
| `Devices[].DeviceIp` | Para LAN | IP do device — filtra qual wake-up UDP dispara a leitura TCP |
| `LocalBindIp` | Recomendado | Interface de rede para receber broadcasts UDP. Em hosts com múltiplas NICs, `0.0.0.0` pode não receber os broadcasts |
| `Endpoint` | Para API | URL base da Tuya OpenAPI (usado apenas pelo `TuyaTokenService`) |
| `AccessId` / `AccessSecret` | Para API | Credenciais da Tuya OpenAPI |

## Modelos

```csharp
record TuyaDevice(string GwId, string Ip, string ProductKey, string Version, bool Encrypt, DateTimeOffset SeenAt);

record SensorReading(
    double Temperature, double Humidity,
    string BatteryState, int BatteryPercent,
    string TempUnit, double HeatIndex,
    string ComfortLevel, DateTimeOffset MeasuredAt);

record TuyaToken(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
```

## Protocolo LAN 55AA (resumo)

```
UDP :6667  →  TuyaLanDiscoveryService  →  evento OnDeviceSeen
                       │ (a cada wake-up do device alvo)
                       ▼
              TuyaLanListenerService
                       │
                       ├─ versão 3.3 ──────────────────────────────────┐
                       │   TCP :6668 (timeout 15s)                      │
                       │   aguarda 250ms após conexão                   │
                       │   CMD 10 DP_QUERY  (até 3 tentativas)         │
                       │   DPs JSON → SensorReading → MQTT retain       │
                       │                                                │
                       └─ versão 3.4 ──────────────────────────────────┘
                           TCP :6668 (timeout 15s)
                           CMD 3 → CMD 4 → CMD 5  (session key)
                           CMD 16 DP_QUERY_NEW
                           até 4 pacotes intermediários descartados
                           DPs JSON → SensorReading → MQTT retain
```

- **UDP broadcast**: payload cifrado com `MD5("yGAdlopoPVldABfn")` (nonce hardcoded Tuya)
- **TCP protocolo 3.3**: payload cifrado com AES-128-ECB; integridade via CRC32
- **TCP protocolo 3.4**: payload cifrado com AES-128-ECB; integridade via HMAC-SHA256
- **Session key (3.4)**: `AES-ECB(local_nonce XOR remote_nonce, local_key)` sem padding
- **Throttle**: intervalo mínimo de 30s entre consultas ao mesmo device
- **Tópico MQTT publicado**: `sensor/tuya/th/{deviceId}`

## Data Points (DPs) suportados

### Sensor TH-01 (protocolo 3.3)

| DP | Chave | Descrição |
|----|-------|-----------|
| 1 | `TempCurrent` | Temperatura raw (÷10 = °C) |
| 2 | `HumidityValue` | Humidade relativa (%) |
| 3 | `BatteryState` | Estado da bateria (texto) |
| 4 | `BatteryPercentage` | Nível da bateria (%) |

### Sensor LCD / RMW002 (protocolo 3.4)

| DP | Chave | Descrição |
|----|-------|-----------|
| 20 | `TempUnit` | Unidade de temperatura (`c` / `f`) |
| 27 | `TempCurrent` | Temperatura raw (÷10 = °C) |
| 46 | `Humidity` | Humidade relativa (%) |
| 101 | `Battery` | Estado da bateria (`high` / `middle` / `low`) |

> Para sensores LCD sem DP numérico de bateria, o percentual é derivado do estado textual: `high=100%`, `middle=50%`, `low=10%`.

## Referência de uso (MqttThSensorWorker)

O projeto `MqttThSensorWorker` é o host Worker Service que consome esta biblioteca. Ele referencia `CamposDev.Tuya.csproj` e usa Serilog para logging estruturado (sinks: Console, File, Seq).

```csharp
// Program.cs (MqttThSensorWorker)
builder.Services.AddTuyaLan(builder.Configuration);
```

## Dependências

- `CamposDev.Mqtt` — `IMqttBrokerService` para publicação MQTT
- `Microsoft.Extensions.Hosting` — `BackgroundService`
- `Microsoft.Extensions.Options` — injeção de configuração
