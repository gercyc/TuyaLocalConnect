using CamposDEV.Mqtt.Services;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CamposDev.Tuya;

/// <summary>
/// Conecta via TCP LAN (porta 6668) a cada sensor T&amp;H Tuya configurado quando ele acorda
/// (detectado pelo broadcast UDP do <see cref="TuyaLanDiscoveryService"/>), realiza a
/// negociação de sessão e lê os DPs diretamente sem nuvem.
/// Suporta protocolo 3.3 (CRC32, sem sessão), 3.4 (HMAC-SHA256, sessão negociada)
/// e 3.5 (envelope 6699 com AES-GCM, sessão negociada).
/// Publica o resultado via MQTT no tópico <c>sensor/tuya/th/{deviceId}</c>.
/// </summary>
public partial class TuyaLanListenerService(
    TuyaLanDiscoveryService discovery,
    IMqttBrokerService mqttService,
    HaDiscoveryPublisher haPublisher,
    TuyaApiOptions options,
    ILogger<TuyaLanListenerService> logger)
    : BackgroundService
{
    // Throttle por device: gwId → última leitura bem-sucedida
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastQuery = new();

    // Uma conexão TCP por device de cada vez: leitura periódica e comando vindo do HA
    // disputam a mesma porta, e muitos devices (sobretudo 3.3) aceitam um cliente só.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceLocks = new();

    private SemaphoreSlim DeviceLock(string deviceId) => _deviceLocks.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Entrada sem DeviceId aparece quando um device sai do appsettings.json mas a
        // chave dele continua no ambiente (TuyaApi__Devices__<N>__LocalKey): o binding
        // cria um Devices[N] só com a LocalKey. Não é device nenhum, então fica de fora.
        var orphans = options.Devices.FindAll(d => string.IsNullOrWhiteSpace(d.DeviceId));
        if (orphans.Count > 0)
            logger.LogWarning(
                "{Count} entrada(s) em TuyaApi.Devices sem DeviceId ignorada(s) — LocalKey órfã no ambiente/secret?",
                orphans.Count);

        var devices = options.Devices.FindAll(d => !string.IsNullOrWhiteSpace(d.DeviceId));
        var configured = devices.FindAll(d => !string.IsNullOrWhiteSpace(d.LocalKey));

        var withoutKey = devices.FindAll(d => string.IsNullOrWhiteSpace(d.LocalKey));
        if (withoutKey.Count > 0)
            logger.LogWarning(
                "{Count} device(s) sem LocalKey serão ignorados (appsettings.local.json ausente?): {Names}",
                withoutKey.Count, string.Join(", ", withoutKey.Select(d => d.DeviceName)));

        if (configured.Count == 0)
        {
            logger.LogWarning(
                "TuyaLanListenerService: nenhum device com LocalKey configurada em TuyaApi.Devices — serviço desativado.");
            return;
        }

        logger.LogInformation(
            "TuyaLanListenerService iniciado — monitorando {Count} device(s): {Ids}",
            configured.Count,
            string.Join(", ", configured.Select(d => d.DeviceId)));

        discovery.OnDeviceSeen += OnDeviceWakeUp;
        await SubscribeCommandsAsync(stoppingToken);

        await PollSilentDevicesAsync(configured, stoppingToken);

        discovery.OnDeviceSeen -= OnDeviceWakeUp;
        logger.LogInformation("TuyaLanListenerService encerrado.");
    }

    private void OnDeviceWakeUp(TuyaDevice device)
    {
        var config = options.FindDevice(device.GwId);
        if (config is null || string.IsNullOrWhiteSpace(config.LocalKey)) return;

        TryQuery(device, config);
    }

    /// <summary>
    /// Dispara a leitura respeitando o intervalo mínimo por device. Broadcast e polling
    /// passam por aqui, então um device nunca é consultado duas vezes no mesmo intervalo.
    /// </summary>
    private void TryQuery(TuyaDevice device, TuyaDeviceConfig config)
    {
        var last = _lastQuery.GetOrAdd(config.DeviceId, DateTimeOffset.MinValue);
        if (DateTimeOffset.Now - last < TuyaProtocolConstants.MinQueryInterval) return;

        _lastQuery[config.DeviceId] = DateTimeOffset.Now;
        _ = Task.Run(() => ConnectAndQueryAsync(device, config));
    }

    // -------------------------------------------------------------------------
    // Polling de devices que não anunciam via UDP
    // -------------------------------------------------------------------------

    /// <summary>
    /// Consulta periodicamente, pelo <c>DeviceIp</c> configurado, os devices genéricos (com
    /// <c>Dps</c>) que não mandaram broadcast recente. Alguns devices (ex.: medidor PJ1103C)
    /// aceitam TCP na 6668 mas nunca anunciam na porta 6667. Sensores a bateria (sem
    /// <c>Dps</c>) ficam de fora: dormem e só aceitam conexão logo após o broadcast.
    /// </summary>
    private async Task PollSilentDevicesAsync(List<TuyaDeviceConfig> configured, CancellationToken stoppingToken)
    {
        var pollable = configured.FindAll(d => d.Dps.Count > 0 && !string.IsNullOrWhiteSpace(d.DeviceIp));
        using var timer = new PeriodicTimer(TuyaProtocolConstants.MinQueryInterval);

        try
        {
            do
            {
                foreach (var config in pollable)
                {
                    if (IsAnnouncingRecently(config.DeviceId)) continue;

                    var device = new TuyaDevice(config.DeviceId, config.DeviceIp!, "", "", true, DateTimeOffset.Now);
                    TryQuery(device, config);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Encerramento normal do worker.
        }
    }

    private bool IsAnnouncingRecently(string deviceId)
        => discovery.Devices.TryGetValue(deviceId, out var seen)
           && DateTimeOffset.Now - seen.SeenAt < TuyaProtocolConstants.BroadcastStaleAfter;

    // -------------------------------------------------------------------------
    // TCP flow: connect → (negociar sessão se 3.4) → query DPs → publish
    // -------------------------------------------------------------------------

    private async Task ConnectAndQueryAsync(TuyaDevice device, TuyaDeviceConfig config)
    {
        // Um comando em andamento já devolve o estado atualizado: a leitura periódica cede a vez.
        var deviceLock = DeviceLock(config.DeviceId);
        if (!await deviceLock.WaitAsync(0)) return;
        try
        {
            await ConnectAndQueryLockedAsync(device, config);
        }
        finally
        {
            deviceLock.Release();
        }
    }

    private async Task ConnectAndQueryLockedAsync(TuyaDevice device, TuyaDeviceConfig config)
    {
        using var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(TuyaProtocolConstants.TcpOverallSecs));

        // O protocolo configurado vence o anunciado: um device pode anunciar uma
        // versão e aceitar outra nas conexões TCP, e a tabela é a fonte confiável.
        var proto = ResolveProtocol(config, device);
        try
        {
            logger.LogDebug(
                "TCP conectando a {Ip}:{Port} [{Name}] protocolo={Proto} (anunciado={Version})",
                device.Ip, TuyaProtocolConstants.TcpLanPort, config.DeviceName, proto, device.Version);

            using var tcp = await ConnectTcpAsync(device.Ip, cts.Token);

            var stream   = tcp.GetStream();
            var localKey = Encoding.UTF8.GetBytes(config.LocalKey);
            var seq      = new SeqNo();

            var dps = proto switch
            {
                TuyaProtocolVersion.V33 => await QueryDps33Async(stream, localKey, seq, config.DeviceId, cts.Token),
                TuyaProtocolVersion.V35 => await QueryDps35Async(stream, localKey, seq, config.DeviceId, cts.Token),
                _                       => await QueryDps34Async(stream, localKey, seq, config.DeviceId, cts.Token)
            };

            // Devices com mapa de DPs viram entidades genéricas no HA; os demais seguem o fluxo T&H.
            if (config.Dps.Count > 0)
            {
                await haPublisher.PublishAsync(config, dps, cts.Token);
                return;
            }

            var reading = MapDpsToReading(dps);
            if (reading is null) return;

            var payload = JsonSerializer.Serialize(reading, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var topic = string.Format(TuyaProtocolConstants.MqttTopicTemplate, config.DeviceId);
            await mqttService.PublishAsync(topic, payload, retain: true);

            logger.LogInformation(
                "Tuya LAN [{Name}] → Temp: {Temp}°C | Humidade: {Humidity}% | Bateria: {Battery}",
                config.DeviceName, reading.Temperature, reading.Humidity, reading.BatteryState);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Timeout TCP para [{Name}] ({Id}) — device provavelmente voltou a dormir",
                config.DeviceName, config.DeviceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha na leitura TCP LAN de [{Name}] ({Id})", config.DeviceName, config.DeviceId);
        }
    }

    // -------------------------------------------------------------------------
    // Protocolo 3.3 — consulta direta sem negociação de sessão
    // -------------------------------------------------------------------------

    private async Task<Dictionary<string, object>> QueryDps33Async(
        NetworkStream stream, byte[] localKey, SeqNo seq, string deviceId, CancellationToken ct,
        bool acceptStatusPush = true)
    {
        await Task.Delay(TuyaProtocolConstants.TcpConnectSettleDelayMs33, ct);

        for (var attempt = 1; attempt <= TuyaProtocolConstants.MaxQueryRetries33; attempt++)
        {
            await SendDpQuery33Async(stream, localKey, seq, deviceId, attempt, ct);

            var result = await ReceiveDpsAsync(stream, localKey, TuyaProtocolVersion.V33, deviceId, ct, acceptStatusPush);
            if (result.Dps is not null)
                return result.Dps;

            if (attempt == TuyaProtocolConstants.MaxQueryRetries33 || !result.ShouldRetry)
                break;

            logger.LogDebug(
                "Sensor 3.3 {DeviceId} pediu retry após resposta transitória; aguardando {Delay}ms",
                deviceId, TuyaProtocolConstants.QueryRetryDelayMs33);
            await Task.Delay(TuyaProtocolConstants.QueryRetryDelayMs33, ct);
        }

        throw new InvalidOperationException(
            $"Nenhuma resposta com DPs válidos do device 3.3 {deviceId} após {TuyaProtocolConstants.MaxQueryRetries33} tentativas");
    }

    // -------------------------------------------------------------------------
    // Protocolo 3.4 — negociação de sessão (3 passos) + consulta
    // -------------------------------------------------------------------------

    private async Task<Dictionary<string, object>> QueryDps34Async(
        NetworkStream stream, byte[] localKey, SeqNo seq, string deviceId, CancellationToken ct)
    {
        var sessionKey = await NegotiateSessionKeyAsync(stream, localKey, seq, ct);
        return await QueryDps34WithSessionAsync(stream, sessionKey, seq, deviceId, ct);
    }

    private async Task<byte[]> NegotiateSessionKeyAsync(
        NetworkStream stream, byte[] localKey, SeqNo seq, CancellationToken ct)
    {
        var localNonce = Encoding.UTF8.GetBytes(TuyaProtocolConstants.LocalNonce34); // 16 bytes

        // Passo 1 — SESS_KEY_NEG_START
        await SendPacketAsync(
            stream, BuildPacket34(TuyaProtocolConstants.Cmd.SessKeyStart, localNonce, localKey, seq), ct);
        logger.LogDebug("SessKey: START enviado");

        // Passo 2 — SESS_KEY_NEG_RESP
        var resp = await ReceivePacketAsync(stream, TuyaProtocolVersion.V34, localKey, ct);
        if (resp.Cmd != TuyaProtocolConstants.Cmd.SessKeyResp)
            throw new InvalidOperationException(
                $"Esperado CMD {TuyaProtocolConstants.Cmd.SessKeyResp}, recebido {resp.Cmd}");

        var decResp      = AesEcbDecrypt(resp.Payload, localKey);
        var remoteNonce  = decResp[..16];
        var theirHmac    = decResp[16..48];
        var expectedHmac = HMACSHA256.HashData(localKey, localNonce);

        if (!theirHmac.SequenceEqual(expectedHmac))
            throw new InvalidOperationException("HMAC do SESS_KEY_NEG_RESP inválido — local_key incorreta?");

        logger.LogDebug("SessKey: RESP recebido, HMAC OK");

        // Passo 3 — SESS_KEY_NEG_FINISH
        var finishPayload = HMACSHA256.HashData(localKey, remoteNonce);
        await SendPacketAsync(
            stream, BuildPacket34(TuyaProtocolConstants.Cmd.SessKeyFinish, finishPayload, localKey, seq), ct);
        logger.LogDebug("SessKey: FINISH enviado");

        // Derivar session_key: AES-ECB(local_nonce XOR remote_nonce, local_key) sem padding
        var xored = new byte[16];
        for (var i = 0; i < 16; i++) xored[i] = (byte)(localNonce[i] ^ remoteNonce[i]);

        var sessionKey = AesEcbEncryptRaw(xored, localKey);
        logger.LogDebug("SessKey: chave de sessão derivada ({Len} bytes)", sessionKey.Length);
        return sessionKey;
    }

    private async Task<Dictionary<string, object>> QueryDps34WithSessionAsync(
        NetworkStream stream, byte[] sessionKey, SeqNo seq, string deviceId, CancellationToken ct,
        bool acceptStatusPush = true)
    {
        var t       = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var jsonObj = new { gwId = deviceId, devId = deviceId, uid = deviceId, t };

        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(jsonObj));
        await SendPacketAsync(
            stream, BuildPacket34(TuyaProtocolConstants.Cmd.DpQueryNew, jsonBytes, sessionKey, seq), ct);
        logger.LogDebug("DP_QUERY_NEW (3.4) enviado para {DeviceId}", deviceId);

        var result = await ReceiveDpsAsync(stream, sessionKey, TuyaProtocolVersion.V34, deviceId, ct, acceptStatusPush);
        return result.Dps ?? throw new InvalidOperationException(
            $"Nenhuma resposta com DPs válidos do device 3.4 {deviceId}");
    }

    // -------------------------------------------------------------------------
    // Protocolo 3.5 — mesmo handshake de 3 passos do 3.4, envelope GCM
    // -------------------------------------------------------------------------

    private async Task<Dictionary<string, object>> QueryDps35Async(
        NetworkStream stream, byte[] localKey, SeqNo seq, string deviceId, CancellationToken ct)
    {
        var sessionKey = await NegotiateSessionKey35Async(stream, localKey, seq, ct);
        return await QueryDps35WithSessionAsync(stream, sessionKey, seq, deviceId, ct);
    }

    private async Task<Dictionary<string, object>> QueryDps35WithSessionAsync(
        NetworkStream stream, byte[] sessionKey, SeqNo seq, string deviceId, CancellationToken ct,
        bool acceptStatusPush = true)
    {
        // No 3.5 o DP_QUERY canônico é o 0x10 com payload vazio, como no 3.4.
        var jsonBytes = "{}"u8.ToArray();
        await SendPacketAsync(
            stream, BuildPacket35(TuyaProtocolConstants.Cmd.DpQueryNew, jsonBytes, sessionKey, seq), ct);
        logger.LogDebug("DP_QUERY_NEW (3.5) enviado para {DeviceId}", deviceId);

        var result = await ReceiveDpsAsync(stream, sessionKey, TuyaProtocolVersion.V35, deviceId, ct, acceptStatusPush);
        return result.Dps ?? throw new InvalidOperationException(
            $"Nenhuma resposta com DPs válidos do device 3.5 {deviceId}");
    }

    private async Task<byte[]> NegotiateSessionKey35Async(
        NetworkStream stream, byte[] localKey, SeqNo seq, CancellationToken ct)
    {
        var localNonce = Encoding.UTF8.GetBytes(TuyaProtocolConstants.LocalNonce34); // 16 bytes

        // Passo 1 — SESS_KEY_NEG_START
        await SendPacketAsync(
            stream, BuildPacket35(TuyaProtocolConstants.Cmd.SessKeyStart, localNonce, localKey, seq), ct);
        logger.LogDebug("SessKey 3.5: START enviado");

        // Passo 2 — SESS_KEY_NEG_RESP. No 3.5 o payload já vem decifrado pela
        // camada de recepção: a tag GCM é verificada lá, junto com o AAD.
        var resp = await ReceivePacketAsync(stream, TuyaProtocolVersion.V35, localKey, ct);
        if (resp.Cmd != TuyaProtocolConstants.Cmd.SessKeyResp)
            throw new InvalidOperationException(
                $"Esperado CMD {TuyaProtocolConstants.Cmd.SessKeyResp}, recebido {resp.Cmd}");

        if (resp.Payload.Length < 48)
            throw new InvalidOperationException(
                $"SESS_KEY_NEG_RESP 3.5 curto demais: {resp.Payload.Length} bytes (esperado ≥ 48)");

        var remoteNonce  = resp.Payload[..16];
        var theirHmac    = resp.Payload[16..48];
        var expectedHmac = HMACSHA256.HashData(localKey, localNonce);

        if (!CryptographicOperations.FixedTimeEquals(theirHmac, expectedHmac))
            throw new InvalidOperationException("HMAC do SESS_KEY_NEG_RESP 3.5 inválido — local_key incorreta?");

        logger.LogDebug("SessKey 3.5: RESP recebido, HMAC OK");

        // Passo 3 — SESS_KEY_NEG_FINISH
        var finishPayload = HMACSHA256.HashData(localKey, remoteNonce);
        await SendPacketAsync(
            stream, BuildPacket35(TuyaProtocolConstants.Cmd.SessKeyFinish, finishPayload, localKey, seq), ct);
        logger.LogDebug("SessKey 3.5: FINISH enviado");

        // A chave de sessão sai de localNonce XOR remoteNonce cifrado com a
        // local_key — mas no 3.5 a cifra é GCM com localNonce[:12], não o ECB
        // do 3.4. Só o keystream importa aqui, então a tag é descartada.
        var xored = new byte[16];
        for (var i = 0; i < 16; i++) xored[i] = (byte)(localNonce[i] ^ remoteNonce[i]);

        var sessionKey = new byte[16];
        var tag        = new byte[TuyaProtocolConstants.GcmTagSize];
        using (var gcm = new AesGcm(localKey, TuyaProtocolConstants.GcmTagSize))
            gcm.Encrypt(localNonce.AsSpan(0, TuyaProtocolConstants.GcmNonceSize), xored, sessionKey, tag);

        logger.LogDebug("SessKey 3.5: chave de sessão derivada ({Len} bytes)", sessionKey.Length);
        return sessionKey;
    }

    /// <summary>
    /// Versão do protocolo a usar na conexão TCP. A configuração explícita vence
    /// o que o device anuncia por UDP; sem configuração, cai no anunciado, e o
    /// padrão histórico (3.4) cobre um anúncio ausente ou desconhecido.
    /// </summary>
    private static TuyaProtocolVersion ResolveProtocol(TuyaDeviceConfig config, TuyaDevice device)
        => ParseProtocol(config.Protocol) ?? ParseProtocol(device.Version) ?? TuyaProtocolVersion.V34;

    private static TuyaProtocolVersion? ParseProtocol(string? version) => version?.Trim() switch
    {
        TuyaProtocolConstants.Version33 => TuyaProtocolVersion.V33,
        TuyaProtocolConstants.Version34 => TuyaProtocolVersion.V34,
        TuyaProtocolConstants.Version35 => TuyaProtocolVersion.V35,
        _                               => null
    };

    // -------------------------------------------------------------------------
    // Recepção de DPs (comum para 3.3 e 3.4)
    // -------------------------------------------------------------------------

    /// <param name="acceptStatusPush">
    /// Aceita o STATUS (CMD 8) que o device envia espontaneamente. Depois de um comando ele traz
    /// só os DPs alterados, então quem precisa do estado completo passa <c>false</c> e espera a
    /// resposta da consulta.
    /// </param>
    private async Task<(Dictionary<string, object>? Dps, bool ShouldRetry)> ReceiveDpsAsync(
        NetworkStream stream, byte[] key, TuyaProtocolVersion proto, string deviceId, CancellationToken ct,
        bool acceptStatusPush = true)
    {
        const int maxAttempts = 4;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var resp = await ReceivePacketAsync(stream, proto, key, ct);

            if ((resp.Cmd != TuyaProtocolConstants.Cmd.Status || !acceptStatusPush) &&
                resp.Cmd != TuyaProtocolConstants.Cmd.DpQuery &&
                resp.Cmd != TuyaProtocolConstants.Cmd.DpQueryNew)
            {
                logger.LogDebug("Pacote intermediário ignorado CMD={Cmd}", resp.Cmd);
                continue;
            }

            byte[] plaintext;
            try
            {
                // No 3.5 o payload já chega decifrado e autenticado: o AAD do GCM
                // são bytes do header, que não sobrevivem ao ReceivePacketAsync.
                plaintext = proto == TuyaProtocolVersion.V35
                    ? resp.Payload
                    : AesEcbDecrypt(resp.Payload, key);
            }
            catch
            {
                logger.LogDebug(
                    "Falha ao decifrar pacote CMD={Cmd} len={Len}, ignorando", resp.Cmd, resp.Payload.Length);
                continue;
            }

            var strippedJson = StripVersionHeader(plaintext);
            var json         = Encoding.UTF8.GetString(strippedJson).TrimEnd('\0');

            if (!json.StartsWith('{'))
            {
                var retryable = proto == TuyaProtocolVersion.V33 && IsRetryable33Error(json);
                logger.LogDebug(
                    "Pacote CMD={Cmd} não é JSON válido, ignorando: {Preview}{RetryHint}",
                    resp.Cmd,
                    json[..Math.Min(json.Length, 20)],
                    retryable ? " [retry 3.3]" : "");

                if (retryable)
                    return (null, ShouldRetry: true);

                continue;
            }

            logger.LogDebug("DPs recebidos: {Json}", json);

            var root = JsonDocument.Parse(json).RootElement;

            if (root.TryGetProperty("data", out var data) && data.TryGetProperty("dps", out var d1))
                return (ParseDps(d1), ShouldRetry: false);
            if (root.TryGetProperty("dps", out var d2))
                return (ParseDps(d2), ShouldRetry: false);

            logger.LogDebug("Resposta sem campo 'dps', ignorando: {Json}", json);
        }

        throw new InvalidOperationException($"Nenhuma resposta com DPs válidos após {maxAttempts} tentativas ({deviceId})");
    }

    private async Task SendDpQuery33Async(
        NetworkStream stream, byte[] localKey, SeqNo seq, string deviceId, int attempt, CancellationToken ct)
    {
        var t       = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var jsonObj = new { gwId = deviceId, devId = deviceId, uid = deviceId, t };

        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(jsonObj));
        var packet    = BuildPacket33(TuyaProtocolConstants.Cmd.DpQuery, jsonBytes, localKey, seq);

        await SendPacketAsync(stream, packet, ct);
        logger.LogDebug("DP_QUERY (3.3) enviado para {DeviceId} tentativa={Attempt}", deviceId, attempt);
    }

    private static bool IsRetryable33Error(string text)
        => text.Contains("json obj data unvali", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object> ParseDps(JsonElement dpsEl)
        => dpsEl.EnumerateObject().ToDictionary(
            p => p.Name,
            p => (object)(p.Value.ValueKind switch
            {
                JsonValueKind.Number => p.Value.GetDouble(),
                JsonValueKind.String => p.Value.GetString() ?? "",
                JsonValueKind.True   => true,
                JsonValueKind.False  => false,
                _                   => p.Value.GetRawText()
            }));

    // -------------------------------------------------------------------------
    // DP → SensorReading
    // Suporta sensor TH-01 (DPs 1,2,3,4 — protocolo 3.3)
    //     e sensor LCD/RMW002 (DPs 20,27,46,101 — protocolo 3.4)
    // -------------------------------------------------------------------------

    private static SensorReading? MapDpsToReading(Dictionary<string, object> dps)
    {
        double? temp = null, humidity = null;
        string? battery = null, unit = null;
        int batteryPct = 0;

        foreach (var (k, v) in dps)
            switch (k)
            {
                // Sensor TH-01 (protocolo 3.3)
                case TuyaProtocolConstants.DpTh01.TempCurrent
                    when v is double t:
                    temp = t / TuyaProtocolConstants.TempScale;
                    break;

                case TuyaProtocolConstants.DpTh01.HumidityValue
                    when v is double h:
                    humidity = h;
                    break;

                case TuyaProtocolConstants.DpTh01.BatteryState:
                    battery = v.ToString();
                    break;

                case TuyaProtocolConstants.DpTh01.BatteryPercentage
                    when v is double pct:
                    batteryPct = (int)pct;
                    break;

                // Sensor LCD / RMW002 (protocolo 3.4)
                case TuyaProtocolConstants.DpLcd.TempUnit:
                    unit = v.ToString();
                    break;

                case TuyaProtocolConstants.DpLcd.TempCurrent
                    when v is double t:
                    temp = t / TuyaProtocolConstants.TempScale;
                    break;

                case TuyaProtocolConstants.DpLcd.Humidity
                    when v is double h:
                    humidity = h;
                    break;

                case TuyaProtocolConstants.DpLcd.Battery:
                    battery = v.ToString();
                    break;
            }

        if (temp is null || humidity is null) return null;

        // Se batteryPct não veio de DP 4, derivar do estado textual (sensor LCD)
        if (batteryPct == 0 && battery is not null)
            batteryPct = battery switch { "high" => 100, "middle" => 50, "low" => 10, _ => 0 };

        return new SensorReading(
            Temperature:    temp.Value,
            Humidity:       humidity.Value,
            BatteryState:   battery ?? "unknown",
            BatteryPercent: batteryPct,
            TempUnit:       unit ?? "c",
            HeatIndex:      HeatIndex(temp.Value, humidity.Value),
            ComfortLevel:   ComfortLevel(temp.Value, humidity.Value),
            MeasuredAt:     DateTimeOffset.UtcNow);
    }

    private static double HeatIndex(double t, double h)
    {
        if (t < 27 || h < 40) return Math.Round(t, 1);
        return Math.Round(-8.78469475556 + 1.61139411 * t + 2.33854883889 * h
            - 0.14611605 * t * h - 0.012308094 * t * t - 0.016424828 * h * h
            + 0.002211732 * t * t * h + 0.00072546 * t * h * h
            - 0.000003582 * t * t * h * h, 1);
    }

    private static string ComfortLevel(double t, double h) =>
        (t > 28 && h > 70) ? "Muito desconfortavel" :
        (t > 26 && h > 60) ? "Desconfortavel" :
        (t is >= 20 and <= 26 && h is >= 40 and <= 60) ? "Confortavel" :
        t < 18 ? "Frio" : "Razoavel";

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Rejeita um frame cujo campo de tamanho excede <see cref="TuyaProtocolConstants.MaxFrameSize"/>,
    /// antes de qualquer alocação. O `length` vem do header, que é controlado pelo
    /// outro lado da conexão: sem este teto, um valor forjado viraria um
    /// <c>new byte[length]</c> de centenas de MB. A conta é feita em <c>long</c>
    /// para que a soma com o header não sofra wraparound.
    /// </summary>
    private static void EnsureFrameFits(uint length, int overhead)
    {
        if ((long)length + overhead > TuyaProtocolConstants.MaxFrameSize)
            throw new InvalidOperationException(
                $"Frame excede o limite de {TuyaProtocolConstants.MaxFrameSize} bytes (length={length})");
    }

    private static byte[] StripVersionHeader(byte[] data)
    {
        if (data.Length <= 15) return data;
        if (data[0] == '3' && data[1] == '.') return data[15..];
        return data;
    }

    private static uint ReadUInt32BE(byte[] data, int offset)
        => (uint)(data[offset] << 24 | data[offset + 1] << 16 |
                  data[offset + 2] << 8  | data[offset + 3]);

    private static void WriteUInt32BE(byte[] buf, int offset, uint value)
    {
        buf[offset]     = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }
}

/// <summary>
/// Versão do protocolo LAN usada numa conexão. Define o envelope do frame,
/// o campo de integridade e se há negociação de sessão.
/// </summary>
enum TuyaProtocolVersion
{
    /// <summary>Envelope 55AA, AES-ECB com a local_key, CRC32. Sem sessão.</summary>
    V33,

    /// <summary>Envelope 55AA, AES-ECB com chave de sessão, HMAC-SHA256.</summary>
    V34,

    /// <summary>Envelope 6699, AES-GCM com chave de sessão. Tag autentica o frame.</summary>
    V35
}

sealed class SeqNo
{
    private int _value = 1;
    public int Next() => _value++;
}
