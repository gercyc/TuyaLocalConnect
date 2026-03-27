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
        const int minSize = TuyaProtocolConstants.HeaderSize
                          + TuyaProtocolConstants.RetcodeSize
                          + TuyaProtocolConstants.CrcEndSize;
        if (data.Length < minSize) return null;

        var prefix = ReadUInt32BE(data, 0);
        if (prefix != TuyaProtocolConstants.Prefix55AA) return null;

        var length        = ReadUInt32BE(data, 12);
        var expectedTotal = TuyaProtocolConstants.HeaderSize + (int)length;
        if (data.Length < expectedTotal) return null;

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
