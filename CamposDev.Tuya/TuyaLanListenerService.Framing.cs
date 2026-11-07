using System.Net.Sockets;
using System.Security.Cryptography;

namespace CamposDev.Tuya;

/// <summary>
/// Framing e criptografia do protocolo LAN Tuya: empacotamento 55AA (3.3/3.4) e 6699 (3.5),
/// recepção de frames, AES-128-ECB e CRC32.
/// </summary>
public partial class TuyaLanListenerService
{
    // -------------------------------------------------------------------------
    // Empacotamento 55AA — protocolo 3.3 (CRC32)
    // -------------------------------------------------------------------------

    private byte[] BuildPacket33(uint cmd, byte[] payload, byte[] key, SeqNo seq)
    {
        // CMD=10 (DP_QUERY) e similares não levam prefixo — device espera JSON puro.
        // CMD=7 (CONTROL) e outros levam "3.3" + 12 nulos EM CLARO, antes do ciphertext:
        // diferente do 3.4, no 3.3 o prefixo fica fora da cifra (como no tinytuya).
        var ciphertext = AesEcbEncrypt(payload, key);
        byte[] encrypted = TuyaProtocolConstants.NoPrefixCmds33.Contains(cmd)
            ? ciphertext
            : [.. TuyaProtocolConstants.VersionHeader33, .. ciphertext];

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
    // Empacotamento 6699 — protocolo 3.5 (AES-GCM)
    //
    // Diferente do 3.3/3.4, não há padding nem campo de integridade separado:
    // o GCM produz ciphertext do mesmo tamanho do payload, mais a tag de 16
    // bytes no fim. O nonce entra em claro no frame, antes do ciphertext.
    // -------------------------------------------------------------------------

    private static byte[] BuildPacket35(uint cmd, byte[] payload, byte[] key, SeqNo seq)
    {
        var bodyLen = TuyaProtocolConstants.GcmNonceSize + payload.Length + TuyaProtocolConstants.GcmTagSize;
        var frame   = new byte[TuyaProtocolConstants.Header35Size + bodyLen + TuyaProtocolConstants.SuffixSize];

        WriteUInt32BE(frame, 0, TuyaProtocolConstants.Prefix6699);
        // frame[4..6] = flags, mantidos zerados.
        WriteUInt32BE(frame, 6, (uint)seq.Next());
        WriteUInt32BE(frame, 10, cmd);
        WriteUInt32BE(frame, TuyaProtocolConstants.LengthOffset35, (uint)bodyLen);

        // Nonce aleatório: em GCM, nonce repetido com a mesma chave quebra a
        // autenticação, então vale gerar por CSPRNG em vez de derivar do seq.
        var nonce = frame.AsSpan(TuyaProtocolConstants.Header35Size, TuyaProtocolConstants.GcmNonceSize);
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = frame.AsSpan(
            TuyaProtocolConstants.Header35Size + TuyaProtocolConstants.GcmNonceSize, payload.Length);
        var tag = frame.AsSpan(
            TuyaProtocolConstants.Header35Size + TuyaProtocolConstants.GcmNonceSize + payload.Length,
            TuyaProtocolConstants.GcmTagSize);
        var aad = frame.AsSpan(4, TuyaProtocolConstants.Aad35Size);

        using var gcm = new AesGcm(key, TuyaProtocolConstants.GcmTagSize);
        gcm.Encrypt(nonce, payload, ciphertext, tag, aad);

        WriteUInt32BE(frame, TuyaProtocolConstants.Header35Size + bodyLen, TuyaProtocolConstants.Suffix6699);
        return frame;
    }

    /// <summary>
    /// Decifra o corpo de um frame 3.5 já lido. Lança se a tag GCM não conferir —
    /// um frame adulterado, ou cifrado com a chave errada, falha explicitamente
    /// em vez de devolver lixo silencioso.
    /// </summary>
    private static byte[] DecryptPayload35(byte[] header, ReadOnlySpan<byte> body, byte[] key)
    {
        var ctLen = body.Length - TuyaProtocolConstants.GcmNonceSize - TuyaProtocolConstants.GcmTagSize;
        if (ctLen < 0)
            throw new InvalidOperationException($"Frame 3.5 truncado: corpo com {body.Length} bytes");

        var nonce      = body[..TuyaProtocolConstants.GcmNonceSize];
        var ciphertext = body.Slice(TuyaProtocolConstants.GcmNonceSize, ctLen);
        var tag        = body.Slice(TuyaProtocolConstants.GcmNonceSize + ctLen, TuyaProtocolConstants.GcmTagSize);
        var aad        = header.AsSpan(4, TuyaProtocolConstants.Aad35Size);

        var plaintext = new byte[ctLen];
        using var gcm = new AesGcm(key, TuyaProtocolConstants.GcmTagSize);
        gcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);

        return StripRetcode35(plaintext);
    }

    /// <summary>
    /// Respostas trazem um retcode de 4 bytes antes do JSON. Detecta pelo próprio
    /// conteúdo: o corpo útil começa em '{' ou é binário (handshake), e nesse caso
    /// o retcode é sempre zero.
    /// </summary>
    private static byte[] StripRetcode35(byte[] plaintext)
        => plaintext.Length > 4 && plaintext[0] == 0 && plaintext[1] == 0 &&
           plaintext[2] == 0 && plaintext[3] == 0
            ? plaintext[4..]
            : plaintext;

    // -------------------------------------------------------------------------
    // Recepção de pacote TCP — suporta 3.3 (CRC32), 3.4 (HMAC) e 3.5 (GCM)
    // -------------------------------------------------------------------------

    private async Task<(uint Cmd, byte[] Payload)> ReceivePacketAsync(
        NetworkStream stream, TuyaProtocolVersion proto, byte[] key, CancellationToken ct)
    {
        if (proto == TuyaProtocolVersion.V35)
            return await ReceivePacket35Async(stream, key, ct);

        var header = await ReadExactAsync(stream, TuyaProtocolConstants.HeaderSize, ct);
        var prefix = ReadUInt32BE(header, 0);
        var cmd    = ReadUInt32BE(header, 8);
        var length = ReadUInt32BE(header, 12);

        if (prefix != TuyaProtocolConstants.Prefix55AA)
            throw new InvalidOperationException($"Prefix inválido: 0x{prefix:X8}");

        EnsureFrameFits(length, TuyaProtocolConstants.HeaderSize);

        var rest = await ReadExactAsync(stream, (int)length, ct);
        // rest = retcode(4) + encrypted_payload + (HMAC(32) ou CRC(4)) + suffix(4)
        var endSize = proto == TuyaProtocolVersion.V34
            ? TuyaProtocolConstants.HmacEndSize
            : TuyaProtocolConstants.CrcEndSize;
        var payloadEnd = rest.Length - endSize;
        var encrypted  = rest[TuyaProtocolConstants.RetcodeSize..payloadEnd];

        logger.LogDebug("Recebido CMD={Cmd} len={Len} proto={Proto}", cmd, encrypted.Length, proto);
        return (cmd, encrypted);
    }

    /// <summary>
    /// Lê um frame 3.5 e devolve o payload JÁ decifrado — diferente do 55AA, onde
    /// a decifragem acontece depois. O AAD do GCM são bytes do próprio header, que
    /// não sobrevivem ao retorno, então a verificação da tag precisa ocorrer aqui.
    /// </summary>
    private async Task<(uint Cmd, byte[] Payload)> ReceivePacket35Async(
        NetworkStream stream, byte[] key, CancellationToken ct)
    {
        var header = await ReadExactAsync(stream, TuyaProtocolConstants.Header35Size, ct);
        var prefix = ReadUInt32BE(header, 0);
        var cmd    = ReadUInt32BE(header, 10);
        var length = ReadUInt32BE(header, TuyaProtocolConstants.LengthOffset35);

        if (prefix != TuyaProtocolConstants.Prefix6699)
            throw new InvalidOperationException($"Prefix 3.5 inválido: 0x{prefix:X8}");

        EnsureFrameFits(length, TuyaProtocolConstants.Header35Size + TuyaProtocolConstants.SuffixSize);

        // `length` cobre nonce + ciphertext + tag; o sufixo vem depois e é lido
        // junto para não deixar bytes órfãos no stream da próxima leitura.
        var rest = await ReadExactAsync(stream, (int)length + TuyaProtocolConstants.SuffixSize, ct);

        var plaintext = DecryptPayload35(header, rest.AsSpan(0, (int)length), key);

        logger.LogDebug("Recebido CMD={Cmd} len={Len} proto=3.5", cmd, plaintext.Length);
        return (cmd, plaintext);
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
}
