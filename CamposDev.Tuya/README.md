# CamposDev.Tuya

Biblioteca .NET 9 para descoberta e leitura de dispositivos Tuya via **protocolo LAN** — sem dependência da nuvem Tuya. Suporta os dois envelopes de frame que a Tuya usa: **55AA** (versões 3.3/3.4) e **6699** (versão 3.5).

## Funcionalidades

| Serviço | Descrição |
|---------|-----------|
| `TuyaLanDiscoveryService` | Escuta broadcasts UDP na porta 6667 e mantém um inventário de dispositivos Tuya ativos na rede. Entende os dois envelopes: 55AA e 6699 |
| `TuyaLanListenerService` | Ao detectar o wake-up de um device configurado, conecta via TCP (porta 6668) e lê os DPs diretamente. Suporta protocolo **3.3** (CRC32, sem sessão), **3.4** (HMAC-SHA256, sessão negociada) e **3.5** (AES-GCM, sessão negociada) |
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

A configuração suporta **múltiplos devices** via array `Devices`. Cada device tem seu próprio `DeviceId`, protocolo e demais parâmetros.

```json
"TuyaApi": {
  "Endpoint":     "https://openapi.tuyaus.com",
  "Devices": [
    {
      "DeviceId":    "eb169bae589e44c6b3fieg",
      "DeviceName":  "T&H Sensor Mobile",
      "DeviceModel": "RMW002",
      "DeviceIp":    "192.168.101.109",
      "Protocol":    "3.4",
      "LocalKey":    "myLocalKey12345"
    },
    {
      "DeviceId":    "ebd259ce7fefdc831ebva4",
      "DeviceName":  "Interruptor Geladeira",
      "DeviceModel": "INT TOMADA NEO",
      "DeviceIp":    "192.168.101.110",
      "Protocol":    "3.5",
      "LocalKey":    "myLocalKey67890"
    }
  ]
}
```

| Campo | Obrigatório | Descrição |
|-------|-------------|-----------|
| `Devices[].DeviceId` | Sim | ID do dispositivo na Tuya (gwId) |
| `Devices[].LocalKey` | Para LAN | Chave AES-128 local do device (16 bytes). Sem ela, o device é ignorado pelo `TuyaLanListenerService` |
| `Devices[].DeviceIp` | Para LAN | IP do device — filtra qual wake-up UDP dispara a leitura TCP |
| `Devices[].Protocol` | Não | Versão do protocolo: `"3.3"`, `"3.4"` ou `"3.5"`. Sem ele, usa a versão anunciada no broadcast UDP; sem anúncio reconhecível, assume `"3.4"` |
| `LocalBindIp` | — | **Ainda não implementado.** A propriedade existe em `TuyaApiOptions`, mas `TuyaLanDiscoveryService` faz bind fixo em `IPAddress.Any` e nunca a lê. Em hosts com múltiplas NICs onde o bind em todas as interfaces não receba os broadcasts, hoje não há como restringir a interface por configuração |
| `Endpoint` | Para API | URL base da Tuya OpenAPI (usado apenas pelo `TuyaTokenService`) |
| `AccessId` / `AccessSecret` | Para API | Credenciais da Tuya OpenAPI |

### Por que configurar `Protocol` explicitamente

A configuração vence a versão anunciada no broadcast. Isso importa porque um device pode anunciar uma versão e aceitar outra nas conexões TCP — fixar o valor na tabela evita depender do anúncio e torna o comportamento previsível.

### Sobrescrevendo valores fora do `appsettings.json`

Qualquer campo pode vir de outra fonte de configuração, o que é útil para manter `LocalKey` e as credenciais da OpenAPI fora de um arquivo versionado. O índice segue a posição do device no array `Devices`:

```bash
# desenvolvimento
dotnet user-secrets set "TuyaApi:Devices:0:LocalKey" "<chave_do_device_0>"

# produção / Docker (`__` como separador)
TuyaApi__Devices__0__LocalKey=<chave_do_device_0>
```

> Reordenar ou remover devices do array desloca os índices — se estiver usando secrets ou variáveis de ambiente, eles precisam ser reajustados junto.

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

## Protocolo LAN (resumo)

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
                       ├─ versão 3.4 ──────────────────────────────────┤
                       │   TCP :6668 (timeout 15s)                      │
                       │   CMD 3 → CMD 4 → CMD 5  (session key, ECB)   │
                       │   CMD 16 DP_QUERY_NEW                          │
                       │   até 4 pacotes intermediários descartados     │
                       │   DPs JSON → SensorReading → MQTT retain       │
                       │                                                │
                       └─ versão 3.5 ──────────────────────────────────┘
                           TCP :6668 (timeout 15s)
                           CMD 3 → CMD 4 → CMD 5  (session key, GCM)
                           CMD 16 DP_QUERY_NEW  (payload "{}")
                           DPs JSON → SensorReading → MQTT retain
```

### Envelopes de frame

Duas famílias de frame convivem nas mesmas portas:

```
55AA (3.3/3.4)
  00 00 55 AA │ seq(4) │ cmd(4) │ len(4) │ payload cifrado │ crc(4) ou hmac(32) │ 00 00 AA 55

6699 (3.5)
  00 00 66 99 │ flags(2) │ seq(4) │ cmd(4) │ len(4) │ nonce(12) │ ciphertext │ tag(16) │ 00 00 99 66
```

| | 3.3 / 3.4 | 3.5 |
|---|---|---|
| Header | 16 bytes (`len` no offset 12) | 18 bytes (`len` no offset 14) |
| Cifra | AES-128-ECB + PKCS#7 | AES-128-GCM, sem padding |
| Integridade | CRC32 (3.3) / HMAC-SHA256 (3.4) | tag GCM de 16 bytes, com AAD |
| Cabeçalho de versão no payload | `"3.x"` + 12 zeros em alguns CMDs | não existe |

### Detalhes por versão

- **UDP broadcast**: payload cifrado com `MD5("yGAdlopoPVldABfn")` (nonce hardcoded Tuya). Devices 3.5 anunciam na **mesma** porta 6667, porém no envelope 6699 — um decodificador que só entenda 55AA descarta esses anúncios em silêncio, e o device nunca é lido apesar de estar ativo
- **TCP protocolo 3.3**: payload cifrado com AES-128-ECB usando a `local_key`; integridade via CRC32; sem sessão
- **TCP protocolo 3.4**: payload cifrado com AES-128-ECB usando a session key; integridade via HMAC-SHA256
- **TCP protocolo 3.5**: AES-128-GCM com a session key; a tag autentica o frame inteiro
  - AAD = os 14 bytes entre o magic e o nonce (flags + seq + cmd + len)
  - `len` conta nonce + ciphertext + tag; o ciphertext tem o tamanho exato do payload
  - Respostas trazem um retcode de 4 bytes zerados antes do JSON
- **Session key (3.4)**: `AES-ECB(local_nonce XOR remote_nonce, local_key)` sem padding
- **Session key (3.5)**: `AES-GCM(local_nonce XOR remote_nonce, local_key, nonce = local_nonce[:12])` — **não** é o ECB do 3.4; trocar um pelo outro é o erro mais comum ao portar
- **Throttle**: intervalo mínimo de 30s entre consultas ao mesmo device
- **Tópico MQTT publicado**: `sensor/tuya/th/{deviceId}`

### Limite de tamanho de frame

O campo `len` vem do header, controlado pelo outro lado da conexão. Frames que o declarem acima de `MaxFrameSize` (8 KB) são rejeitados **antes** de qualquer alocação, e a conta é feita em `long` para que um valor forjado não sofra wraparound. Sem isso, um device comprometido — ou um pacote forjado na LAN — faria o processo alocar centenas de MB.

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
