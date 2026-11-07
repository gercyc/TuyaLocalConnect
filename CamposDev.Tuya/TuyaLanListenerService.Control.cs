using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CamposDev.Tuya;

/// <summary>
/// Controle dos devices a partir do Home Assistant: recebe comandos MQTT, envia
/// CONTROL (3.3) ou CONTROL_NEW (3.4/3.5) e publica o estado relido do device.
/// </summary>
public partial class TuyaLanListenerService
{
    /// <summary>Tempo máximo esperando uma leitura em andamento no mesmo device liberar a conexão.</summary>
    private static readonly TimeSpan CommandLockTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Versão do payload JSON do CONTROL_NEW, a mesma que o tinytuya usa no 3.4/3.5.</summary>
    private const int ControlNewProtocol = 5;

    // O device confirma o comando antes de aplicá-lo: uma consulta imediata ainda devolve o
    // valor anterior. Relê algumas vezes até os DPs comandados refletirem o valor pedido.
    private const int ControlConfirmAttempts = 3;
    private static readonly TimeSpan ControlConfirmDelay = TimeSpan.FromMilliseconds(300);

    // Token de parada do host: comandos em curso abortam no shutdown em vez de segurá-lo.
    private CancellationToken _stoppingToken;

    private Task SubscribeCommandsAsync(CancellationToken ct)
    {
        _stoppingToken = ct;
        if (!options.Devices.Exists(d => d.Dps.Count > 0)) return Task.CompletedTask;

        logger.LogInformation("Aguardando comandos do HA em {Filter}", haPublisher.CommandTopicFilter);
        return mqttService.SubscribeAsync(haPublisher.CommandTopicFilter, OnCommandAsync, ct);
    }

    // Tópico: {StateTopicPrefix}/{deviceId}/{código}/set — lido pelo fim, porque o prefixo pode ter '/'.
    private async Task OnCommandAsync(string topic, string payload)
    {
        var levels = topic.Split('/');
        if (levels.Length < 4) return;

        var deviceId = levels[^3];
        var code     = levels[^2];

        var config = options.FindDevice(deviceId);
        var dpId   = config?.Dps.FirstOrDefault(kv => kv.Value.Code == code).Key;
        if (config is null || dpId is null)
        {
            logger.LogWarning("Comando ignorado: device {Id} ou código {Code} não configurado", deviceId, code);
            return;
        }

        var value = HaDiscoveryPublisher.ParseCommand(config, code, payload);
        if (value is null)
        {
            logger.LogWarning("Comando ignorado para [{Name}] {Code}: payload '{Payload}' inválido ou DP não controlável",
                config.DeviceName, code, payload);
            return;
        }

        logger.LogInformation("Comando HA → [{Name}] {Code} (DP {DpId}) = {Value}", config.DeviceName, code, dpId, value);
        await SendCommandAsync(config, new Dictionary<string, object> { [dpId] = value });
    }

    private async Task SendCommandAsync(TuyaDeviceConfig config, Dictionary<string, object> dps)
    {
        discovery.Devices.TryGetValue(config.DeviceId, out var seen);
        var ip = string.IsNullOrWhiteSpace(config.DeviceIp) ? seen?.Ip : config.DeviceIp;
        if (string.IsNullOrWhiteSpace(ip))
        {
            logger.LogWarning("Comando para [{Name}] sem IP: configure DeviceIp ou aguarde o broadcast UDP", config.DeviceName);
            return;
        }

        var deviceLock = DeviceLock(config.DeviceId);
        if (!await deviceLock.WaitAsync(CommandLockTimeout, _stoppingToken))
        {
            logger.LogWarning("Comando para [{Name}] descartado: device ocupado por mais de {Secs}s",
                config.DeviceName, CommandLockTimeout.TotalSeconds);
            return;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
            cts.CancelAfter(TimeSpan.FromSeconds(TuyaProtocolConstants.TcpOverallSecs));
            var device = seen ?? new TuyaDevice(config.DeviceId, ip, "", "", true, DateTimeOffset.Now);
            var proto  = ResolveProtocol(config, device);

            using var tcp = await ConnectTcpAsync(ip, cts.Token);
            var stream    = tcp.GetStream();
            var localKey  = Encoding.UTF8.GetBytes(config.LocalKey);
            var seq       = new SeqNo();

            var state = proto switch
            {
                TuyaProtocolVersion.V33 => await Control33Async(stream, localKey, seq, config.DeviceId, dps, cts.Token),
                TuyaProtocolVersion.V35 => await Control35Async(stream, localKey, seq, config.DeviceId, dps, cts.Token),
                _                       => await Control34Async(stream, localKey, seq, config.DeviceId, dps, cts.Token)
            };

            // O estado acabou de ser lido: a leitura periódica não precisa repetir agora.
            _lastQuery[config.DeviceId] = DateTimeOffset.Now;
            await haPublisher.PublishAsync(config, state, cts.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Timeout TCP enviando comando para [{Name}] ({Id})", config.DeviceName, config.DeviceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha enviando comando para [{Name}] ({Id})", config.DeviceName, config.DeviceId);
        }
        finally
        {
            deviceLock.Release();
        }
    }

    private static async Task<TcpClient> ConnectTcpAsync(string ip, CancellationToken ct)
    {
        var tcp = new TcpClient
        {
            NoDelay        = true,
            ReceiveTimeout = TuyaProtocolConstants.TcpTimeoutMs,
            SendTimeout    = TuyaProtocolConstants.TcpTimeoutMs
        };
        try
        {
            await tcp.ConnectAsync(ip, TuyaProtocolConstants.TcpLanPort, ct);
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Envio do comando por versão. Depois do envio, cada um relê os DPs na mesma
    // conexão, ignorando o STATUS espontâneo (que traz só o DP alterado).
    // -------------------------------------------------------------------------

    private async Task<Dictionary<string, object>> Control33Async(
        NetworkStream stream, byte[] localKey, SeqNo seq, string deviceId, Dictionary<string, object> dps, CancellationToken ct)
    {
        await Task.Delay(TuyaProtocolConstants.TcpConnectSettleDelayMs33, ct);

        var t       = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { devId = deviceId, uid = deviceId, t, dps });

        await SendPacketAsync(stream, BuildPacket33(TuyaProtocolConstants.Cmd.Control, payload, localKey, seq), ct);
        logger.LogDebug("CONTROL (3.3) enviado para {DeviceId}", deviceId);

        return await ConfirmControlAsync(dps, deviceId,
            () => QueryDps33Async(stream, localKey, seq, deviceId, ct, acceptStatusPush: false), ct);
    }

    private async Task<Dictionary<string, object>> Control34Async(
        NetworkStream stream, byte[] localKey, SeqNo seq, string deviceId, Dictionary<string, object> dps, CancellationToken ct)
    {
        var sessionKey = await NegotiateSessionKeyAsync(stream, localKey, seq, ct);

        // CONTROL_NEW está fora de NoPrefixCmds34: o BuildPacket34 já prefixa "3.4"+zeros.
        await SendPacketAsync(
            stream, BuildPacket34(TuyaProtocolConstants.Cmd.ControlNew, ControlNewPayload(dps), sessionKey, seq), ct);
        logger.LogDebug("CONTROL_NEW (3.4) enviado para {DeviceId}", deviceId);

        return await ConfirmControlAsync(dps, deviceId,
            () => QueryDps34WithSessionAsync(stream, sessionKey, seq, deviceId, ct, acceptStatusPush: false), ct);
    }

    private async Task<Dictionary<string, object>> Control35Async(
        NetworkStream stream, byte[] localKey, SeqNo seq, string deviceId, Dictionary<string, object> dps, CancellationToken ct)
    {
        var sessionKey = await NegotiateSessionKey35Async(stream, localKey, seq, ct);

        byte[] payload = [.. TuyaProtocolConstants.VersionHeader35, .. ControlNewPayload(dps)];
        await SendPacketAsync(
            stream, BuildPacket35(TuyaProtocolConstants.Cmd.ControlNew, payload, sessionKey, seq), ct);
        logger.LogDebug("CONTROL_NEW (3.5) enviado para {DeviceId}", deviceId);

        return await ConfirmControlAsync(dps, deviceId,
            () => QueryDps35WithSessionAsync(stream, sessionKey, seq, deviceId, ct, acceptStatusPush: false), ct);
    }

    /// <summary>
    /// Relê os DPs até os valores comandados aparecerem. Sem confirmação após as tentativas,
    /// devolve a última leitura: o estado publicado é sempre o que o device reportou.
    /// </summary>
    private async Task<Dictionary<string, object>> ConfirmControlAsync(
        Dictionary<string, object> commanded, string deviceId,
        Func<Task<Dictionary<string, object>>> query, CancellationToken ct)
    {
        Dictionary<string, object> state = [];
        for (var attempt = 1; attempt <= ControlConfirmAttempts; attempt++)
        {
            await Task.Delay(ControlConfirmDelay, ct);
            try
            {
                state = await query();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < ControlConfirmAttempts)
            {
                // O comando pode já ter sido aplicado: uma releitura falha não descarta as próximas.
                logger.LogDebug(ex, "Releitura {Attempt} após comando falhou em {DeviceId}", attempt, deviceId);
                continue;
            }

            var applied = commanded.All(kv => state.TryGetValue(kv.Key, out var v) && Equals(v, kv.Value));
            if (applied) return state;
        }

        logger.LogWarning("Device {DeviceId} não refletiu o comando após {Attempts} leituras: {Dps}",
            deviceId, ControlConfirmAttempts, JsonSerializer.Serialize(commanded));
        return state;
    }

    private static byte[] ControlNewPayload(Dictionary<string, object> dps)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocol = ControlNewProtocol,
            t        = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            data     = new { dps }
        });
}
