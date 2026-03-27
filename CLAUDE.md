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

**TuyaLocalConnect** é uma solução .NET 9 para leitura de sensores Tuya (temperatura/umidade) diretamente via protocolo LAN 55AA, sem depender da nuvem Tuya. Publica leituras via MQTT.

### Projetos

- **`CamposDev.Tuya/`** — Biblioteca reutilizável com todos os serviços Tuya LAN. Pode ser empacotada como NuGet.
- **`MqttThSensorWorker/`** — Worker Service .NET 9 que consome a biblioteca. Configura DI, Serilog e MQTT.

### Fluxo de dados

```
Broadcast UDP (porta 6667)
    → TuyaLanDiscoveryService  (descriptografa, mantém inventário em ConcurrentDictionary)
    → evento OnDeviceSeen
    → TuyaLanListenerService   (TCP porta 6668, negocia sessão, consulta DPs)
    → SensorReading
    → MQTT: sensor/tuya/th/{deviceId}  (retain=true)
```

### Serviços principais

| Serviço | Responsabilidade |
|---------|-----------------|
| `TuyaLanDiscoveryService` | Listener UDP 6667, descriptografa broadcasts, dispara `OnDeviceSeen` |
| `TuyaLanListenerService` | Conecta TCP 6668, implementa protocolos 3.3 e 3.4, analisa DPs, publica MQTT |
| `TuyaTokenService` | Gerencia token Tuya OpenAPI (fetch + refresh automático) |

### Criptografia e protocolos

- **UDP discovery**: AES-128-ECB com chave = `MD5("yGAdlopoPVldABfn")`
- **TCP protocolo 3.3**: AES-128-ECB com `local_key`, integridade CRC32 — sem sessão
- **TCP protocolo 3.4**: AES-128-ECB com session key, integridade HMAC-SHA256, handshake de 3 passos
  - Session key derivada por: `AES-ECB(local_nonce XOR remote_nonce, local_key)` sem padding

A seleção do protocolo é automática baseada na versão anunciada no broadcast UDP.

### Comportamentos importantes

- **Throttle**: mínimo 30s entre consultas por dispositivo (evita flood no sensor)
- **Retry**: protocolo 3.3 tenta até 3 vezes com 400ms de intervalo em erros "json obj data unvali"
- **Timeout TCP**: 15s total, 5s por operação de I/O
- **DPs suportados**:
  - TH-01 (3.3): DPs 1, 2, 3, 4 — Temperatura, Umidade, EstadoBateria, %Bateria
  - RMW002/LCD (3.4): DPs 20, 27, 46, 101 — UnidadeTemp, TempAtual, Umidade, Bateria

### Configuração

Configurado via `appsettings.json` na seção `"TuyaApi"`. Cada dispositivo requer `DeviceId`, `DeviceName`, `DeviceModel`, `LocalKey` e `DeviceIp`. Dados sensíveis (LocalKey, credenciais Tuya OpenAPI) devem usar `dotnet user-secrets`.

A biblioteca exige que `IMqttBrokerService` (de `CamposDev.Mqtt`) esteja registrado antes de `AddTuyaLan()`.

### Gerenciamento de pacotes NuGet

Versões centralizadas em `Directory.Packages.props` — sempre adicionar/atualizar versões ali, nunca diretamente nos `.csproj`.
