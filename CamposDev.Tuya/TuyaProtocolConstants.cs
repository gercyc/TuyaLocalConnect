using System.Text;

namespace CamposDev.Tuya;

/// <summary>
/// Constantes do protocolo Tuya LAN (55AA) — versões 3.3 e 3.4.
/// </summary>
internal static class TuyaProtocolConstants
{
    // -------------------------------------------------------------------------
    // Portas
    // -------------------------------------------------------------------------

    /// <summary>Porta UDP de descoberta de dispositivos Tuya (broadcasts).</summary>
    public const int UdpDiscoveryPort = 6667;

    /// <summary>Porta TCP de comunicação LAN com dispositivos Tuya.</summary>
    public const int TcpLanPort = 6668;

    // -------------------------------------------------------------------------
    // Framing do protocolo 55AA
    // -------------------------------------------------------------------------

    public const uint Prefix55AA = 0x000055AA;
    public const uint Suffix55AA = 0x0000AA55;

    public const int HeaderSize  = 16;
    public const int RetcodeSize = 4;
    public const int HmacSize    = 32;
    public const int CrcSize     = 4;
    public const int SuffixSize  = 4;

    /// <summary>Tamanho do sufixo com HMAC-SHA256 (protocolo 3.4): 32 HMAC + 4 suffix.</summary>
    public const int HmacEndSize = HmacSize + SuffixSize; // 36

    /// <summary>Tamanho do sufixo com CRC32 (protocolo 3.3): 4 CRC + 4 suffix.</summary>
    public const int CrcEndSize = CrcSize + SuffixSize; // 8

    // -------------------------------------------------------------------------
    // Versões de protocolo
    // -------------------------------------------------------------------------

    public const string Version33 = "3.3";
    public const string Version34 = "3.4";

    /// <summary>
    /// Cabeçalho de versão 3.3 no payload antes da encriptação (3 bytes versão + 12 nulos = 15 bytes).
    /// Usado em todos os comandos do protocolo 3.3.
    /// </summary>
    public static readonly byte[] VersionHeader33 =
        [.. Encoding.UTF8.GetBytes("3.3"), .. new byte[12]];

    /// <summary>
    /// Cabeçalho de versão 3.4 no payload antes da encriptação (3 bytes versão + 12 nulos = 15 bytes).
    /// Usado apenas em comandos de controle do protocolo 3.4 (não em negociação de sessão).
    /// </summary>
    public static readonly byte[] VersionHeader34 =
        [.. Encoding.UTF8.GetBytes("3.4"), .. new byte[12]];

    // -------------------------------------------------------------------------
    // Descriptografia UDP
    // -------------------------------------------------------------------------

    /// <summary>
    /// Nonce hardcoded do protocolo Tuya LAN para descriptografar broadcasts UDP.
    /// A chave real é MD5 desta string.
    /// </summary>
    public const string UdpNonceString = "yGAdlopoPVldABfn";

    // -------------------------------------------------------------------------
    // CMDs do protocolo LAN Tuya
    // REF: https://github.com/tuya/tuya-iotos-embeded-sdk-wifi-ble-bk7231n/blob/master/sdk/include/lan_protocol.h
    // -------------------------------------------------------------------------

    public static class Cmd
    {
        public const uint SessKeyStart  = 3;  // FRM_SECURITY_TYPE3 — iniciar negociação de sessão
        public const uint SessKeyResp   = 4;  // FRM_SECURITY_TYPE4 — resposta de negociação
        public const uint SessKeyFinish = 5;  // FRM_SECURITY_TYPE5 — finalizar negociação
        public const uint Control       = 7;  // FRM_TP_CMD — enviar comando
        public const uint Status        = 8;  // FRM_TP_STAT_REPORT — status/leitura do device
        public const uint HeartBeat     = 9;  // FRM_TP_HB
        public const uint DpQuery       = 10; // FRM_QUERY_STAT — consultar DPs (protocolo 3.3)
        public const uint DpQueryNew    = 16; // FRM_QUERY_STAT_NEW — consultar DPs (protocolo 3.4)
        public const uint UpdateDps     = 18; // FRM_LAN_QUERY_DP — forçar atualização de DPs
    }

    /// <summary>
    /// CMDs do protocolo 3.3 que NÃO levam prefixo de versão no payload.
    /// CMD=10 (DP_QUERY) não usa prefixo — device espera JSON puro antes da encriptação.
    /// </summary>
    public static readonly HashSet<uint> NoPrefixCmds33 =
    [
        Cmd.HeartBeat, Cmd.DpQuery, Cmd.DpQueryNew, Cmd.UpdateDps
    ];

    /// <summary>
    /// CMDs do protocolo 3.4 que NÃO levam prefixo de versão no payload
    /// (negociação de sessão e consultas de dados).
    /// </summary>
    public static readonly HashSet<uint> NoPrefixCmds34 =
    [
        Cmd.SessKeyStart, Cmd.SessKeyResp, Cmd.SessKeyFinish,
        Cmd.HeartBeat, Cmd.DpQuery, Cmd.DpQueryNew, Cmd.UpdateDps
    ];

    // -------------------------------------------------------------------------
    // Timeouts
    // -------------------------------------------------------------------------

    public const int TcpTimeoutMs   = 5_000; // ms por operação de read/write
    public const int TcpOverallSecs = 15;    // timeout total da conexão
    public const int TcpConnectSettleDelayMs33 = 250;
    public const int QueryRetryDelayMs33       = 400;
    public const int MaxQueryRetries33         = 3;

    /// <summary>Intervalo mínimo entre consultas ao mesmo device.</summary>
    public static readonly TimeSpan MinQueryInterval = TimeSpan.FromSeconds(30);

    // -------------------------------------------------------------------------
    // Negociação de sessão (protocolo 3.4)
    // -------------------------------------------------------------------------

    /// <summary>Nonce local fixo de 16 bytes enviado no SESS_KEY_NEG_START.</summary>
    public const string LocalNonce34 = "0123456789abcdef";

    // -------------------------------------------------------------------------
    // Data Points (DPs) — mapeamento por modelo de sensor
    // -------------------------------------------------------------------------

    /// <summary>
    /// DPs do sensor TH-01 (WiFi Temperature &amp; Humidity Sensor).
    /// Protocolo 3.3. Temperatura com escala 1 (dividir por 10).
    /// </summary>
    public static class DpTh01
    {
        public const string TempCurrent       = "1";
        public const string HumidityValue     = "2";
        public const string BatteryState      = "3";
        public const string BatteryPercentage = "4";
    }

    /// <summary>
    /// DPs do sensor LCD (RMW002 e similares).
    /// Protocolo 3.4. Temperatura com escala 1 (dividir por 10).
    /// </summary>
    public static class DpLcd
    {
        public const string TempUnit    = "20";
        public const string TempCurrent = "27";
        public const string Humidity    = "46";
        public const string Battery     = "101";
    }

    /// <summary>Fator de escala da temperatura (valor raw / 10 = graus Celsius).</summary>
    public const double TempScale = 10.0;

    // -------------------------------------------------------------------------
    // Tópico MQTT
    // -------------------------------------------------------------------------

    /// <summary>Template do tópico MQTT para publicação de leituras de sensor.</summary>
    public const string MqttTopicTemplate = "sensor/tuya/th/{0}";
}
