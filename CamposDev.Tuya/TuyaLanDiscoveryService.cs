using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CamposDev.Tuya;

/// <summary>
/// Escuta broadcasts UDP Tuya LAN (porta 6667) e mantém um inventário de dispositivos
/// descobertos na rede. O payload de anúncio é cifrado com a chave nonce hardcoded Tuya.
/// Entende os dois envelopes que convivem nessa porta: 55AA (3.3/3.4, AES-ECB) e
/// 6699 (3.5, AES-GCM).
/// </summary>
public class TuyaLanDiscoveryService(
    ILogger<TuyaLanDiscoveryService> logger)
    : BackgroundService
{
    // MD5("yGAdlopoPVldABfn") — nonce hardcoded do protocolo Tuya LAN para broadcasts UDP
    private static readonly byte[] NonceKey =
        MD5.HashData(Encoding.UTF8.GetBytes(TuyaProtocolConstants.UdpNonceString));

    // Inventário em memória: gwId → última leitura
    private readonly ConcurrentDictionary<string, TuyaDevice> _devices = new();

    /// <summary>Snapshot dos dispositivos descobertos (cópia imutável).</summary>
    public IReadOnlyDictionary<string, TuyaDevice> Devices => _devices;

    /// <summary>Disparado a cada pacote recebido de um device (novo ou heartbeat).</summary>
    public event Action<TuyaDevice>? OnDeviceSeen;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "TuyaLanDiscoveryService iniciado — escutando UDP:{Port}",
            TuyaProtocolConstants.UdpDiscoveryPort);

        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, TuyaProtocolConstants.UdpDiscoveryPort));

        using var reg = stoppingToken.Register(() => udp.Close());

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro ao receber pacote UDP.");
                continue;
            }

            HandlePacket(result.Buffer, result.RemoteEndPoint.Address);
        }

        logger.LogInformation("TuyaLanDiscoveryService encerrado. {Count} dispositivo(s) descoberto(s).", _devices.Count);
    }

    private void HandlePacket(byte[] data, IPAddress sourceIp)
    {
        var announcement = TryDecode(data);
        if (announcement is null) return;

        var gwId = announcement.GwId;
        if (string.IsNullOrWhiteSpace(gwId)) return;

        var isNew = !_devices.ContainsKey(gwId);

        var device = new TuyaDevice(
            GwId:       gwId,
            Ip:         announcement.Ip ?? sourceIp.ToString(),
            ProductKey: announcement.ProductKey ?? "",
            Version:    announcement.Version ?? "",
            Encrypt:    announcement.Encrypt,
            SeenAt:     DateTimeOffset.Now);

        _devices[gwId] = device;
        OnDeviceSeen?.Invoke(device);

        if (isNew)
            logger.LogInformation(
                "Novo dispositivo Tuya descoberto → gwId={GwId} ip={Ip} version={Version} encrypt={Encrypt} productKey={ProductKey}",
                gwId, device.Ip, device.Version, device.Encrypt, device.ProductKey);
        else
            logger.LogDebug(
                "Heartbeat Tuya → gwId={GwId} ip={Ip}", gwId, device.Ip);
    }

    // -------------------------------------------------------------------------
    // Decode
    // -------------------------------------------------------------------------

    private TuyaAnnouncement? TryDecode(byte[] data)
    {
        if (data.Length < 4) return null;

        // Devices 3.5 anunciam na MESMA porta 6667, mas com o envelope próprio
        // (prefixo 00006699 + AES-GCM). Ignorar isso faz eles nunca aparecerem,
        // mesmo estando ativos: o anúncio chega e é descartado em silêncio.
        // A chave do broadcast é a mesma nos dois formatos.
        var prefix = ReadUInt32BE(data, 0);
        if (prefix == TuyaProtocolConstants.Prefix6699) return TryDecode35(data);

        const int minSize = TuyaProtocolConstants.HeaderSize
                          + TuyaProtocolConstants.RetcodeSize
                          + TuyaProtocolConstants.CrcEndSize;
        if (data.Length < minSize) return null;
        if (prefix != TuyaProtocolConstants.Prefix55AA) return null;

        // Conta em long antes do cast: um `length` forjado (uint alto) viraria um
        // int negativo, e `expectedTotal` menor que data.Length passaria pela
        // checagem abaixo levando a um range inválido no slice seguinte.
        var length = ReadUInt32BE(data, 12);
        if ((long)TuyaProtocolConstants.HeaderSize + length > data.Length) return null;
        var expectedTotal = TuyaProtocolConstants.HeaderSize + (int)length;

        var ciphertext = data[
            (TuyaProtocolConstants.HeaderSize + TuyaProtocolConstants.RetcodeSize)
            ..(expectedTotal - TuyaProtocolConstants.CrcEndSize)];
        if (ciphertext.Length == 0 || ciphertext.Length % 16 != 0) return null;

        try
        {
            using var aes = Aes.Create();
            aes.Mode    = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key     = NonceKey;

            using var decryptor = aes.CreateDecryptor();
            var plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            var json      = Encoding.UTF8.GetString(plaintext).TrimEnd('\0');

            if (!json.StartsWith('{')) return null;

            return JsonSerializer.Deserialize<TuyaAnnouncement>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decodifica um anúncio no envelope 6699 (protocolo 3.5):
    /// magic(4) + flags(2) + seq(4) + cmd(4) + len(4) + nonce(12) + ct + tag(16) + suffix(4).
    /// O AAD do GCM são os 14 bytes entre o magic e o nonce.
    /// </summary>
    private TuyaAnnouncement? TryDecode35(byte[] data)
    {
        const int minSize = TuyaProtocolConstants.Header35Size
                          + TuyaProtocolConstants.GcmNonceSize
                          + TuyaProtocolConstants.GcmTagSize;
        if (data.Length < minSize) return null;

        var length = ReadUInt32BE(data, TuyaProtocolConstants.LengthOffset35);
        if (length < TuyaProtocolConstants.GcmNonceSize + TuyaProtocolConstants.GcmTagSize) return null;
        // Conta em long: `length` é uint vindo do header, e a soma em aritmética
        // uint sofreria wraparound num valor forjado, passando pela checagem.
        if ((long)TuyaProtocolConstants.Header35Size + length > data.Length) return null;

        var ctLen = (int)length - TuyaProtocolConstants.GcmNonceSize - TuyaProtocolConstants.GcmTagSize;

        try
        {
            var nonce = data.AsSpan(TuyaProtocolConstants.Header35Size, TuyaProtocolConstants.GcmNonceSize);
            var ct    = data.AsSpan(
                TuyaProtocolConstants.Header35Size + TuyaProtocolConstants.GcmNonceSize, ctLen);
            var tag   = data.AsSpan(
                TuyaProtocolConstants.Header35Size + TuyaProtocolConstants.GcmNonceSize + ctLen,
                TuyaProtocolConstants.GcmTagSize);
            var aad   = data.AsSpan(4, TuyaProtocolConstants.Aad35Size);

            var plaintext = new byte[ctLen];
            using var gcm = new AesGcm(NonceKey, TuyaProtocolConstants.GcmTagSize);
            gcm.Decrypt(nonce, ct, tag, plaintext, aad);

            // Respostas trazem um retcode de 4 bytes zerados antes do JSON.
            var body = plaintext.Length > 4 && plaintext[0] == 0 && plaintext[1] == 0 &&
                       plaintext[2] == 0 && plaintext[3] == 0
                ? plaintext[4..]
                : plaintext;

            var json = Encoding.UTF8.GetString(body).TrimEnd('\0');
            if (!json.StartsWith('{')) return null;

            return JsonSerializer.Deserialize<TuyaAnnouncement>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static uint ReadUInt32BE(byte[] data, int offset)
        => (uint)(data[offset] << 24 | data[offset + 1] << 16 |
                  data[offset + 2] << 8  | data[offset + 3]);
}

class TuyaAnnouncement
{
    [JsonPropertyName("gwId")]       public string? GwId       { get; set; }
    [JsonPropertyName("ip")]         public string? Ip         { get; set; }
    [JsonPropertyName("productKey")] public string? ProductKey { get; set; }
    [JsonPropertyName("version")]    public string? Version    { get; set; }
    [JsonPropertyName("encrypt")]    public bool    Encrypt    { get; set; }
}
