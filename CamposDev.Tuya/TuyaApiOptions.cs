namespace CamposDev.Tuya;

/// <summary>
/// Configuração de um dispositivo Tuya individual.
/// </summary>
public class TuyaDeviceConfig
{
    /// <summary>ID do device na Tuya (campo gwId dos broadcasts UDP).</summary>
    public string DeviceId { get; set; } = "";

    public string DeviceName { get; set; } = "Tuya T&H Sensor";

    public string DeviceModel { get; set; } = "RMW002";

    /// <summary>
    /// Chave local AES-128 do device (campo local_key retornado pela API Tuya).
    /// Necessária para negociar a sessão TCP LAN.
    /// </summary>
    public string LocalKey { get; set; } = "";

    /// <summary>
    /// IP local do device. Usado como filtro de wake-up UDP e destino da conexão TCP.
    /// Opcional — se vazio, o primeiro broadcast com o DeviceId correto será aceito.
    /// </summary>
    public string? DeviceIp { get; set; }
}

/// <summary>
/// Configuração global da integração Tuya.
/// </summary>
public class TuyaApiOptions
{
    // -------------------------------------------------------------------------
    // Configurações da Tuya OpenAPI (usadas pelo TuyaTokenService)
    // -------------------------------------------------------------------------

    public string Endpoint { get; set; } = "https://openapi.tuyaus.com";
    public string AccessId { get; set; } = "";
    public string AccessSecret { get; set; } = "";

    // -------------------------------------------------------------------------
    // Configurações LAN
    // -------------------------------------------------------------------------

    /// <summary>
    /// IP da interface de rede local usada para receber broadcasts UDP Tuya (porta 6667).
    /// Se vazio, usa 0.0.0.0 (todas as interfaces). Em hosts com múltiplas NICs (Wi-Fi,
    /// Ethernet, VPN) é recomendado especificar a interface correta.
    /// </summary>
    public string? LocalBindIp { get; set; }

    /// <summary>
    /// Lista de dispositivos Tuya monitorados via protocolo LAN.
    /// </summary>
    public List<TuyaDeviceConfig> Devices { get; set; } = [];

    // -------------------------------------------------------------------------
    // Helpers internos
    // -------------------------------------------------------------------------

    /// <summary>
    /// Retorna a configuração do device pelo gwId, ou null se não estiver na lista.
    /// </summary>
    internal TuyaDeviceConfig? FindDevice(string gwId)
        => Devices.Find(d => d.DeviceId == gwId);
}
