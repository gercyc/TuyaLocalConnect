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

    /// <summary>
    /// Versão do protocolo LAN do device: <c>"3.3"</c>, <c>"3.4"</c> ou <c>"3.5"</c>.
    /// Opcional — se vazio, usa a versão anunciada no broadcast UDP. Configurar
    /// explicitamente evita depender do broadcast e é útil quando o device anuncia
    /// uma versão diferente da que aceita nas conexões TCP.
    /// </summary>
    public string? Protocol { get; set; }

    /// <summary>
    /// Categoria Tuya do device (campo <c>category</c> da API: <c>kg</c>, <c>cz</c>, <c>pc</c>…).
    /// Opcional — usada para refinar a apresentação das entidades no Home Assistant.
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// Mapa número do DP → código Tuya (ex.: <c>"1": { "Code": "switch_1" }</c>).
    /// Quando preenchido, o device é publicado como entidades genéricas via MQTT Discovery
    /// do Home Assistant, no padrão do localtuya. Quando vazio, segue o fluxo de sensor T&amp;H.
    /// </summary>
    public Dictionary<string, TuyaDpConfig> Dps { get; set; } = [];
}

/// <summary>
/// Configuração de um Data Point: o código Tuya define o tipo de entidade no HA.
/// </summary>
public class TuyaDpConfig
{
    /// <summary>Código Tuya do DP (ex.: <c>switch_1</c>, <c>cur_power</c>, <c>add_ele</c>).</summary>
    public string Code { get; set; } = "";

    /// <summary>Nome da entidade no HA. Opcional — sem ele, vale o nome padrão do localtuya.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Fator de escala do valor numérico. Opcional — sem ele, vale o padrão do localtuya para o código
    /// (ex.: 0.1 para <c>cur_power</c>). Necessário quando o device usa outra escala na nuvem.
    /// </summary>
    public double? Scale { get; set; }
}

/// <summary>
/// Publicação no Home Assistant via MQTT Discovery.
/// </summary>
public class HomeAssistantOptions
{
    /// <summary>Prefixo de discovery configurado no HA (padrão do HA: <c>homeassistant</c>).</summary>
    public string DiscoveryPrefix { get; set; } = "homeassistant";

    /// <summary>Prefixo dos tópicos de estado: <c>{StateTopicPrefix}/{deviceId}/state</c>.</summary>
    public string StateTopicPrefix { get; set; } = "tuya";

    /// <summary>
    /// Segundos sem atualização até o HA marcar a entidade como indisponível
    /// (<c>expire_after</c>). 0 desativa.
    /// </summary>
    public int ExpireAfterSeconds { get; set; } = 300;
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

    /// <summary>
    /// Publicação das entidades no Home Assistant (MQTT Discovery).
    /// </summary>
    public HomeAssistantOptions HomeAssistant { get; set; } = new();

    // -------------------------------------------------------------------------
    // Helpers internos
    // -------------------------------------------------------------------------

    /// <summary>
    /// Retorna a configuração do device pelo gwId, ou null se não estiver na lista.
    /// </summary>
    internal TuyaDeviceConfig? FindDevice(string gwId)
        => Devices.Find(d => d.DeviceId == gwId);
}
