# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Comandos

```bash
# Build da solução
dotnet build

# Executar o worker (produção)
dotnet run --project MqttThSensorWorker

# Executar em modo desenvolvimento
dotnet run --project MqttThSensorWorker --environment Development

# Publicar
dotnet publish MqttThSensorWorker -c Release

# Configurar secrets locais (para dados sensíveis)
dotnet user-secrets --project MqttThSensorWorker set "TuyaApi:Endpoint" "https://openapi.tuyaus.com"
```

Não há projetos de teste neste repositório.

## Arquitetura

**TuyaLocalConnect** é uma solução .NET 9 para leitura de sensores Tuya (temperatura/umidade) diretamente via protocolo LAN, sem depender da nuvem Tuya. Suporta as versões 3.3, 3.4 e 3.5 — as duas primeiras usam o envelope de frame `55AA`, a última usa o `6699` com AES-GCM. Publica leituras via MQTT.

### Projetos

- **`CamposDev.Tuya/`** — Biblioteca reutilizável com todos os serviços Tuya LAN. Pode ser empacotada como NuGet.
- **`MqttThSensorWorker/`** — Worker Service .NET 9 que consome a biblioteca. Configura DI, Serilog e MQTT.
- **`TuyaEsp32Meter/`** — Firmware ESP32 (PlatformIO, `pio run -t upload`) com o mesmo protocolo, só leitura: medidor PJ1103C, disjuntor TMPH e tomada medidora cz. Devices são cadastrados pela página web e ficam na NVS (sem recompilar). Publica em `tuya32/...` no mosquitto do diamond (`192.168.18.137`), que faz bridge para o `192.168.101.3`. Ver `TuyaEsp32Meter/README.md`. Wi-Fi/MQTT de primeiro boot em `include/secrets.h` (gitignored).

### Fluxo de dados

```
Broadcast UDP (porta 6667)
    → TuyaLanDiscoveryService  (descriptografa, mantém inventário em ConcurrentDictionary)
    → evento OnDeviceSeen
    → TuyaLanListenerService   (TCP porta 6668, negocia sessão, consulta DPs)
    ├─ device sem Dps → SensorReading → MQTT: sensor/tuya/th/{deviceId}  (retain=true)
    └─ device com Dps → HaDiscoveryPublisher
                          → homeassistant/{switch|binary_sensor|sensor}/{deviceId}/{código}/config  (retain, 1x por execução)
                          → tuya/{deviceId}/state  (retain, JSON por código de DP, valores escalados)

Comando do HA: tuya/{deviceId}/{código}/set (ON/OFF)
    → TuyaLanListenerService.Control  (lock por device, CONTROL 3.3 / CONTROL_NEW 3.4-3.5)
    → relê os DPs até refletirem o comando → HaDiscoveryPublisher → tuya/{deviceId}/state
```

### Serviços principais

| Serviço | Responsabilidade |
|---------|-----------------|
| `TuyaLanDiscoveryService` | Listener UDP 6667, descriptografa broadcasts (55AA e 6699), dispara `OnDeviceSeen` |
| `TuyaLanListenerService` | Conecta TCP 6668, implementa protocolos 3.3, 3.4 e 3.5, analisa DPs, publica MQTT. Partials: `.Control.cs` (comandos do HA) e `.Framing.cs` (frames 55AA/6699, AES, CRC32) |
| `HaDiscoveryPublisher` | Publica devices genéricos (plugs, interruptores) via MQTT Discovery do HA, no padrão de entidades do localtuya |
| `HaEntityCatalog` | Código de DP → entidade HA (nome, device_class, unidade, escala), copiado das tabelas `kg`/`cz`/`pc` do localtuya |
| `TuyaTokenService` | Gerencia token Tuya OpenAPI (fetch + refresh automático) |

### Criptografia e protocolos

Dois envelopes de frame convivem na mesma porta:

- **55AA** (3.3/3.4): `00 00 55 AA | seq(4) | cmd(4) | len(4) | payload | crc-ou-hmac | 00 00 AA 55`
- **6699** (3.5): `00 00 66 99 | flags(2) | seq(4) | cmd(4) | len(4) | nonce(12) | ciphertext | tag(16) | 00 00 99 66`

Detalhes por versão:

- **UDP discovery**: chave = `MD5("yGAdlopoPVldABfn")`. Devices 3.5 anunciam na **mesma** porta 6667, mas no envelope 6699 — ignorar isso faz o device nunca aparecer, mesmo estando ativo.
- **TCP protocolo 3.3**: AES-128-ECB com `local_key`, integridade CRC32 — sem sessão
- **TCP protocolo 3.4**: AES-128-ECB com session key, integridade HMAC-SHA256, handshake de 3 passos
  - Session key derivada por: `AES-ECB(local_nonce XOR remote_nonce, local_key)` sem padding
- **TCP protocolo 3.5**: AES-128-GCM com session key; a tag de 16 bytes autentica o frame
  - AAD = os 14 bytes entre o magic e o nonce (flags + seq + cmd + len); `len` conta nonce + ciphertext + tag
  - Sem PKCS#7 (GCM é cifra de fluxo) e **sem** cabeçalho de versão no payload
  - Mesmo handshake de 3 passos do 3.4, mas a session key é derivada por
    `AES-GCM(local_nonce XOR remote_nonce, local_key, nonce=local_nonce[:12])` — **não** por ECB
  - Respostas trazem um retcode de 4 bytes zerados antes do JSON

A seleção do protocolo usa o campo `Protocol` do device em `appsettings.json`; se ausente, cai na versão anunciada no broadcast UDP, e por fim em 3.4. A configuração explícita vence o anúncio, porque um device pode anunciar uma versão e aceitar outra nas conexões TCP.

Frames cujo campo de tamanho excede `MaxFrameSize` (8 KB) são rejeitados antes de qualquer alocação: esse campo vem do header, que é controlado pelo outro lado da conexão.

### Comportamentos importantes

- **Throttle**: mínimo 30s entre consultas por dispositivo (evita flood no sensor)
- **Polling sem broadcast**: devices com `Dps` e `DeviceIp` que não anunciaram via UDP no último minuto são consultados a cada 30s pelo IP configurado (ex.: o medidor PJ1103C, que aceita TCP mas nunca anuncia — hoje lido pelo `TuyaEsp32Meter`, não mais pelo worker). Sensores sem `Dps` (a bateria) ficam de fora, porque dormem
- **Um cliente TCP por device**: rodar o worker local junto com o container de produção faz os dois disputarem a porta 6668, e aparecem timeouts e `connection reset` que não existem com uma instância só
- **Retry**: protocolo 3.3 tenta até 3 vezes com 400ms de intervalo em erros "json obj data unvali"
- **Timeout TCP**: 15s total, 5s por operação de I/O
- **DPs suportados**:
  - TH-01 (3.3): DPs 1, 2, 3, 4 — Temperatura, Umidade, EstadoBateria, %Bateria
  - RMW002/LCD (3.4): DPs 20, 27, 46, 101 — UnidadeTemp, TempAtual, Umidade, Bateria
  - Demais devices: pelo mapa `Dps` (número → código Tuya) na config; a entidade sai do `HaEntityCatalog`
- **Controle**: só `switch` (incluindo `child_lock`, `overcharge_switch`, `switch_backlight`). `select`/`number` do localtuya ainda são `sensor` com entity_category `diagnostic` (o HA não aceita `config` em sensores)
  - 3.3: CONTROL (7), cabeçalho `3.3`+12 zeros **em claro antes** do ciphertext
  - 3.4: CONTROL_NEW (13), cabeçalho `3.4`+12 zeros **dentro** da cifra
  - 3.5: CONTROL_NEW (13), cabeçalho `3.5`+12 zeros prefixado ao plaintext do GCM (só no comando; o DP_QUERY_NEW vai sem)
  - O device confirma (CMD 13) antes de aplicar: a releitura imediata devolve o valor antigo, por isso `ConfirmControlAsync` relê até 3x com 300ms
  - Leitura periódica e comando compartilham um `SemaphoreSlim` por device; a leitura cede a vez se houver comando em curso
- **CamposDev.Mqtt 1.1.0** (fonte em `C:\source\gercyc\Microservices\CamposDev.Microservice\CamposDev.Mqtt`): retain de fato aplicado (a 1.0.1 ignorava o parâmetro), `SubscribeAsync` e `AvailabilityTopic` com Last Will. Consumida de um feed local via `nuget.config` até ser publicada no nuget.org
- **Referência de entidades**: o fork xZetsubou do localtuya, em `core/ha_entities/*.py`

### Configuração

Configurado via `appsettings.json` na seção `"TuyaApi"`. Cada dispositivo requer `DeviceId`, `DeviceName`, `DeviceModel`, `DeviceIp`, `LocalKey` e `Protocol` (`"3.3"`, `"3.4"` ou `"3.5"`; opcional — sem ele vale a versão anunciada no broadcast). Devices genéricos levam também `Category` (opcional) e `Dps` (`{"1": {"Code": "switch_1", "Name": "...", "Scale": 0.1}}`); `TuyaApi:HomeAssistant` define prefixos de tópico e `ExpireAfterSeconds`.

As `LocalKey` **não vão para o git** (o repositório é público). No `appsettings.json` elas ficam vazias. Os valores reais estão em `MqttThSensorWorker/appsettings.local.json`, que está no `.gitignore` e tem a mesma ordem do array `Devices`, com `DeviceId` + `LocalKey`. O merge é por índice: ao adicionar, remover ou reordenar devices, ajuste os dois arquivos juntos. Nunca coloque chave real no `appsettings.json`, no `docker-compose.yml` ou nos READMEs.

O `Program.cs` carrega o arquivo local antes de variáveis de ambiente e argumentos, que continuam vencendo (`TuyaApi__Devices__0__LocalKey`, user-secrets). O publish copia o arquivo, então ele entra na imagem `homeserver:32081/tuya-local-connect`. O registry é da LAN; não publique a imagem em registry público. Device sem chave é ignorado, com warning no log.

A biblioteca exige que `IMqttBrokerService` (de `CamposDev.Mqtt`) esteja registrado antes de `AddTuyaLan()`.

### Gerenciamento de pacotes NuGet

Versões centralizadas em `Directory.Packages.props` — sempre adicionar/atualizar versões ali, nunca diretamente nos `.csproj`.
