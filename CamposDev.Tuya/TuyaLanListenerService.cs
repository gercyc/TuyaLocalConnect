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
/// Suporta protocolo 3.3 (CRC32, sem sessão) e 3.4 (HMAC-SHA256, sessão negociada).
/// Publica o resultado via MQTT no tópico <c>sensor/tuya/th/{deviceId}</c>.
/// </summary>
public class TuyaLanListenerService(
    TuyaLanDiscoveryService discovery,
    IMqttBrokerService mqttService,
    TuyaApiOptions options,
    ILogger<TuyaLanListenerService> logger)
    : BackgroundService
{
    // Throttle por device: gwId → última leitura bem-sucedida
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastQuery = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configured = options.Devices.FindAll(d => !string.IsNullOrWhiteSpace(d.LocalKey));

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

        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        discovery.OnDeviceSeen -= OnDeviceWakeUp;
        logger.LogInformation("TuyaLanListenerService encerrado.");
    }

    private void OnDeviceWakeUp(TuyaDevice device)
    {
        var config = options.FindDevice(device.GwId);
        if (config is null || string.IsNullOrWhiteSpace(config.LocalKey)) return;

        var last = _lastQuery.GetOrAdd(device.GwId, DateTimeOffset.MinValue);
        if (DateTimeOffset.Now - last < TuyaProtocolConstants.MinQueryInterval) return;

        _lastQuery[device.GwId] = DateTimeOffset.Now;
        _ = Task.Run(() => ConnectAndQueryAsync(device, config));
    }

    // -------------------------------------------------------------------------
    // TCP flow: connect → (negociar sessão se 3.4) → query DPs → publish
    // -------------------------------------------------------------------------

    private async Task ConnectAndQueryAsync(TuyaDevice device, TuyaDeviceConfig config)
    {
        using var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(TuyaProtocolConstants.TcpOverallSecs));
        try
        {
            logger.LogDebug(
                "TCP conectando a {Ip}:{Port} [{Name}] protocolo={Version}",
                device.Ip, TuyaProtocolConstants.TcpLanPort, config.DeviceName, device.Version);

            using var tcp = new TcpClient();
            tcp.NoDelay = true;
            tcp.ReceiveTimeout = TuyaProtocolConstants.TcpTimeoutMs;
            tcp.SendTimeout    = TuyaProtocolConstants.TcpTimeoutMs;
            await tcp.ConnectAsync(device.Ip, TuyaProtocolConstants.TcpLanPort, cts.Token);

            var stream   = tcp.GetStream();
            var localKey = Encoding.UTF8.GetBytes(config.LocalKey);
            var seq      = new SeqNo();

            Dictionary<string, object> dps;

            if (device.Version == TuyaProtocolConstants.Version33)
                dps = await QueryDps33Async(stream, localKey, seq, config.DeviceId, cts.Token);
            else
                dps = await QueryDps34Async(stream, localKey, seq, config.DeviceId, cts.Token);

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
        NetworkStream stream, byte[] localKey, SeqNo seq, string deviceId, CancellationToken ct)
    {
        await Task.Delay(TuyaProtocolConstants.TcpConnectSettleDelayMs33, ct);

        for (var attempt = 1; attempt <= TuyaProtocolConstants.MaxQueryRetries33; attempt++)
        {
            await SendDpQuery33Async(stream, localKey, seq, deviceId, attempt, ct);

            var result = await ReceiveDpsAsync(stream, localKey, isProto34: false, deviceId, ct);
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
        var resp = await ReceivePacketAsync(stream, isProto34: true, ct);
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
        NetworkStream stream, byte[] sessionKey, SeqNo seq, string deviceId, CancellationToken ct)
    {
        var t       = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var jsonObj = new { gwId = deviceId, devId = deviceId, uid = deviceId, t };

        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(jsonObj));
        await SendPacketAsync(
            stream, BuildPacket34(TuyaProtocolConstants.Cmd.DpQueryNew, jsonBytes, sessionKey, seq), ct);
        logger.LogDebug("DP_QUERY_NEW (3.4) enviado para {DeviceId}", deviceId);

        var result = await ReceiveDpsAsync(stream, sessionKey, isProto34: true, deviceId, ct);
        return result.Dps ?? throw new InvalidOperationException(
            $"Nenhuma resposta com DPs válidos do device 3.4 {deviceId}");
    }

    // -------------------------------------------------------------------------
    // Recepção de DPs (comum para 3.3 e 3.4)
    // -------------------------------------------------------------------------

    private async Task<(Dictionary<string, object>? Dps, bool ShouldRetry)> ReceiveDpsAsync(
        NetworkStream stream, byte[] key, bool isProto34, string deviceId, CancellationToken ct)
    {
        const int maxAttempts = 4;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var resp = await ReceivePacketAsync(stream, isProto34, ct);

            if (resp.Cmd != TuyaProtocolConstants.Cmd.Status &&
                resp.Cmd != TuyaProtocolConstants.Cmd.DpQuery &&
                resp.Cmd != TuyaProtocolConstants.Cmd.DpQueryNew)
            {
                logger.LogDebug("Pacote intermediário ignorado CMD={Cmd}", resp.Cmd);
                continue;
            }

            byte[] plaintext;
            try
            {
                plaintext = AesEcbDecrypt(resp.Payload, key);
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
                var retryable = !isProto34 && IsRetryable33Error(json);
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
    // Empacotamento 55AA — protocolo 3.3 (CRC32)
    // -------------------------------------------------------------------------

    private byte[] BuildPacket33(uint cmd, byte[] payload, byte[] key, SeqNo seq)
    {
        // CMD=10 (DP_QUERY) e similares não levam prefixo — device espera JSON puro.
        // CMD=7 (CONTROL) e outros levam "3.3" + 12 nulos antes da encriptação.
        byte[] toEncrypt = TuyaProtocolConstants.NoPrefixCmds33.Contains(cmd)
            ? payload
            : [.. TuyaProtocolConstants.VersionHeader33, .. payload];
        var encrypted = AesEcbEncrypt(toEncrypt, key);

        var length = (uint)(encrypted.Length + TuyaProtocolConstants.CrcEndSize);

        var header = new byte[TuyaProtocolConstants.HeaderSize];
        WriteUInt32BE(header, 0, TuyaProtocolConstants.Prefix55AA);
        WriteUInt32BE(header, 4, (uint)seq.Next());
        WriteUInt32BE(header, 8, cmd);
        WriteUInt32BE(header, 12, length);

        var body = new byte[header.Length + encrypted.Length];
        header.CopyTo(body, 0);
        encrypted.CopyTo(body, header.Length);

        var crc         = Crc32(body);
        var crcBytes    = new byte[4];
        var suffixBytes = new byte[4];
        WriteUInt32BE(crcBytes, 0, crc);
        WriteUInt32BE(suffixBytes, 0, TuyaProtocolConstants.Suffix55AA);

        return [.. body, .. crcBytes, .. suffixBytes];
    }

    // -------------------------------------------------------------------------
    // Empacotamento 55AA — protocolo 3.4 (HMAC-SHA256)
    // -------------------------------------------------------------------------

    private byte[] BuildPacket34(uint cmd, byte[] payload, byte[] key, SeqNo seq)
    {
        var toEncrypt = TuyaProtocolConstants.NoPrefixCmds34.Contains(cmd)
            ? payload
            : [.. TuyaProtocolConstants.VersionHeader34, .. payload];

        var encrypted = AesEcbEncrypt(toEncrypt, key);
        var length    = (uint)(encrypted.Length + TuyaProtocolConstants.HmacEndSize);

        var header = new byte[TuyaProtocolConstants.HeaderSize];
        WriteUInt32BE(header, 0, TuyaProtocolConstants.Prefix55AA);
        WriteUInt32BE(header, 4, (uint)seq.Next());
        WriteUInt32BE(header, 8, cmd);
        WriteUInt32BE(header, 12, length);

        var dataForHmac = new byte[header.Length + encrypted.Length];
        header.CopyTo(dataForHmac, 0);
        encrypted.CopyTo(dataForHmac, header.Length);

        var hmac        = HMACSHA256.HashData(key, dataForHmac);
        var suffixBytes = new byte[4];
        WriteUInt32BE(suffixBytes, 0, TuyaProtocolConstants.Suffix55AA);

        return [.. dataForHmac, .. hmac, .. suffixBytes];
    }

    // -------------------------------------------------------------------------
    // Recepção de pacote TCP — suporta 3.3 (CRC32) e 3.4 (HMAC)
    // -------------------------------------------------------------------------

    private async Task<(uint Cmd, byte[] Payload)> ReceivePacketAsync(
        NetworkStream stream, bool isProto34, CancellationToken ct)
    {
        var header = await ReadExactAsync(stream, TuyaProtocolConstants.HeaderSize, ct);
        var prefix = ReadUInt32BE(header, 0);
        var cmd    = ReadUInt32BE(header, 8);
        var length = ReadUInt32BE(header, 12);

        if (prefix != TuyaProtocolConstants.Prefix55AA)
            throw new InvalidOperationException($"Prefix inválido: 0x{prefix:X8}");

        var rest = await ReadExactAsync(stream, (int)length, ct);
        // rest = retcode(4) + encrypted_payload + (HMAC(32) ou CRC(4)) + suffix(4)
        var endSize    = isProto34 ? TuyaProtocolConstants.HmacEndSize : TuyaProtocolConstants.CrcEndSize;
        var payloadEnd = rest.Length - endSize;
        var encrypted  = rest[TuyaProtocolConstants.RetcodeSize..payloadEnd];

        logger.LogDebug("Recebido CMD={Cmd} len={Len} proto={Proto}", cmd, encrypted.Length,
            isProto34 ? "3.4" : "3.3");
        return (cmd, encrypted);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buf    = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buf.AsMemory(offset, count - offset), ct);
            if (read == 0) throw new EndOfStreamException("Conexão encerrada pelo device.");
            offset += read;
        }
        return buf;
    }

    private static Task SendPacketAsync(NetworkStream stream, byte[] packet, CancellationToken ct)
        => stream.WriteAsync(packet, ct).AsTask();

    // -------------------------------------------------------------------------
    // Criptografia AES-128-ECB
    // -------------------------------------------------------------------------

    private static byte[] AesEcbEncrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.PKCS7; aes.Key = key;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] AesEcbEncryptRaw(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = key;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] AesEcbDecrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.PKCS7; aes.Key = key;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 0, data.Length);
    }

    // -------------------------------------------------------------------------
    // CRC32 (protocolo 3.3)
    // -------------------------------------------------------------------------

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = (crc >> 8) ^ Crc32Table[(crc ^ b) & 0xFF];
        return crc ^ 0xFFFFFFFFu;
    }

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

sealed class SeqNo
{
    private int _value = 1;
    public int Next() => _value++;
}
