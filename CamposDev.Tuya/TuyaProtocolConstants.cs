using System.Text;

namespace CamposDev.Tuya;

/// <summary>
/// Constantes do protocolo Tuya LAN — versões 3.3 e 3.4 (envelope 55AA)
/// e 3.5 (envelope 6699 com AES-GCM).
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
    // Framing do protocolo 6699 (versão 3.5)
    //
    // Envelope próprio, distinto do 55AA:
    //   00 00 66 99 | flags(2) | seq(4) | cmd(4) | len(4) |
    //               | nonce(12) | ciphertext | tag(16) | 00 00 99 66
    //
    // A cifra é AES-128-GCM. O AAD são os 14 bytes entre o magic e o nonce
    // (flags + seq + cmd + len), e `len` conta nonce + ciphertext + tag.
    // Não há PKCS#7: o GCM é de fluxo, o ciphertext tem o tamanho exato do
    // payload.
    // -------------------------------------------------------------------------

    public const uint Prefix6699 = 0x00006699;
    public const uint Suffix6699 = 0x00009966;

    /// <summary>magic(4) + flags(2) + seq(4) + cmd(4) + len(4).</summary>
    public const int Header35Size = 18;

    /// <summary>AAD do GCM: do byte 4 ao 17 do frame (flags + seq + cmd + len).</summary>
    public const int Aad35Size = 14;

    /// <summary>Offset do campo `len` no header 3.5 (o 55AA tem em 12).</summary>
    public const int LengthOffset35 = 14;

    public const int GcmNonceSize = 12;
    public const int GcmTagSize   = 16;

    /// <summary>
    /// Frame maior que isso é descartado sem alocar. O campo de tamanho vem do
    /// header, que não é confiável: sem um teto, um device comprometido (ou um
    /// pacote forjado na LAN) faria o processo alocar centenas de MB.
    /// O DP_QUERY de uma tomada responde ~400 bytes; 8 KB dão folga larga para
    /// devices com muitos DPs sem abrir espaço para abuso.
    /// </summary>
    public const int MaxFrameSize = 8 * 1024;

    // -------------------------------------------------------------------------
    // Versões de protocolo
    // -------------------------------------------------------------------------

    public const string Version33 = "3.3";
    public const string Version34 = "3.4";
    public const string Version35 = "3.5";

    /// <summary>
    /// Cabeçalho de versão 3.3 (3 bytes versão + 12 nulos = 15 bytes). No 3.3 ele vai em claro,
    /// prefixado ao ciphertext, nos comandos fora de <see cref="NoPrefixCmds33"/> (ex.: CONTROL).
    /// </summary>
    public static readonly byte[] VersionHeader33 =
        [.. Encoding.UTF8.GetBytes("3.3"), .. new byte[12]];

    /// <summary>
    /// Cabeçalho de versão 3.4 no payload antes da encriptação (3 bytes versão + 12 nulos = 15 bytes).
    /// Usado apenas em comandos de controle do protocolo 3.4 (não em negociação de sessão).
    /// </summary>
    public static readonly byte[] VersionHeader34 =
        [.. Encoding.UTF8.GetBytes("3.4"), .. new byte[12]];

    /// <summary>
    /// Cabeçalho de versão 3.5, prefixado ao plaintext antes do GCM apenas no CONTROL_NEW.
    /// Consultas e negociação de sessão vão sem ele (ver <see cref="NoPrefixCmds35"/>).
    /// </summary>
    public static readonly byte[] VersionHeader35 =
        [.. Encoding.UTF8.GetBytes("3.5"), .. new byte[12]];

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
        public const uint ControlNew    = 13; // CONTROL_NEW — enviar comando (protocolos 3.4 e 3.5)
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
    // Negociação de sessão (protocolo 3.5)
    // -------------------------------------------------------------------------

    /// <summary>
    /// No 3.5, consultas e negociação de sessão vão sem prefixo de versão: injetar
    /// "3.5"+zeros num DP_QUERY_NEW corrompe o payload. A exceção é o CONTROL_NEW,
    /// que leva <see cref="VersionHeader35"/> (montado em quem envia o comando).
    /// </summary>
    public static readonly HashSet<uint> NoPrefixCmds35 = [];

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
