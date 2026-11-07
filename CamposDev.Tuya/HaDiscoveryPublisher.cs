using CamposDEV.Mqtt.Services;
using CamposDEV.Mqtt.Settings;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CamposDev.Tuya;

/// <summary>
/// Publica devices Tuya genéricos (plugs, interruptores, sensores) no Home Assistant via
/// MQTT Discovery, no padrão de entidades do localtuya.
/// </summary>
/// <remarks>
/// Cada device vira um device no HA com uma entidade por DP mapeado em <c>Dps</c>. As configs de
/// discovery são publicadas com retain na primeira leitura bem-sucedida de cada execução. O estado
/// vai em JSON único por device em <c>{StateTopicPrefix}/{deviceId}/state</c>, com chaves pelo
/// código do DP e valores já escalados. Switches recebem comandos em
/// <c>{StateTopicPrefix}/{deviceId}/{código}/set</c>.
/// </remarks>
public sealed class HaDiscoveryPublisher(
    IMqttBrokerService mqttService,
    TuyaApiOptions options,
    ILogger<HaDiscoveryPublisher> logger,
    MqttBrokerSettings? brokerSettings = null)
{
    private const int    ValueDecimals = 6;
    private const string PayloadOn     = "ON";
    private const string PayloadOff    = "OFF";

    // Payloads MQTT não são HTML: o encoder relaxado mantém aspas simples dos templates
    // Jinja e acentos dos nomes legíveis para quem inspeciona o broker.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Devices cujo discovery já foi publicado nesta execução: deviceId → (vazio)
    private readonly ConcurrentDictionary<string, byte> _announced = new();

    // Último valor conhecido de cada código, por device. Uma leitura parcial (ex.: STATUS
    // com só o DP alterado) atualiza o que veio e mantém o resto, e o JSON publicado
    // sempre traz todas as entidades anunciadas.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, object?>> _state = new();

    private HomeAssistantOptions Ha => options.HomeAssistant;

    /// <summary>
    /// Publica o estado dos DPs lidos e, na primeira vez, o discovery das entidades.
    /// </summary>
    public async Task PublishAsync(
        TuyaDeviceConfig config, IReadOnlyDictionary<string, object> dps, CancellationToken ct)
    {
        var entities = ResolveEntities(config);
        if (entities.Count == 0)
        {
            logger.LogWarning(
                "Device [{Name}] ({Id}) não tem DPs mapeados para entidades HA — nada publicado. DPs: {Dps}",
                config.DeviceName, config.DeviceId, string.Join(", ", dps.Keys));
            return;
        }

        if (_announced.TryAdd(config.DeviceId, 0))
        {
            try
            {
                await PublishDiscoveryAsync(config, entities, ct);
            }
            catch
            {
                _announced.TryRemove(config.DeviceId, out _); // tenta de novo na próxima leitura
                throw;
            }
        }

        var state = UpdateState(config.DeviceId, entities, dps);
        await mqttService.PublishAsync(StateTopic(config.DeviceId), state, true, ct);

        logger.LogInformation("Tuya LAN [{Name}] → {State}", config.DeviceName, state);
    }

    /// <summary>
    /// Converte o payload de um comando do HA no valor bruto do DP, ou null se o código não
    /// for controlável ou o payload for inválido.
    /// </summary>
    public static object? ParseCommand(TuyaDeviceConfig config, string code, string payload)
    {
        var definition = HaEntityCatalog.Find(code, config.Category);
        if (definition?.Component != HaComponent.Switch) return null;

        return payload.Trim().ToUpperInvariant() switch
        {
            PayloadOn  => true,
            PayloadOff => false,
            _          => null
        };
    }

    /// <summary>Tópico de comando de uma entidade controlável.</summary>
    public string CommandTopic(string deviceId, string code) => $"{Ha.StateTopicPrefix}/{deviceId}/{code}/set";

    /// <summary>Filtro que casa os tópicos de comando de todos os devices.</summary>
    public string CommandTopicFilter => $"{Ha.StateTopicPrefix}/+/+/set";

    // -------------------------------------------------------------------------
    // Resolução DP → entidade
    // -------------------------------------------------------------------------

    private sealed record ResolvedEntity(string DpId, TuyaDpConfig Dp, HaEntityDefinition Definition);

    /// <summary>
    /// Entidades vêm da configuração, não da leitura: um DP ausente numa resposta não some do HA.
    /// </summary>
    private List<ResolvedEntity> ResolveEntities(TuyaDeviceConfig config)
    {
        var resolved = new List<ResolvedEntity>();
        var codes    = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (dpId, dp) in config.Dps)
        {
            if (string.IsNullOrWhiteSpace(dp.Code)) continue;

            var definition = HaEntityCatalog.Find(dp.Code, config.Category);
            if (definition is null)
            {
                logger.LogDebug("[{Name}] DP {DpId} ({Code}) sem entidade HA no catálogo — ignorado",
                    config.DeviceName, dpId, dp.Code);
                continue;
            }

            if (!codes.Add(dp.Code))
            {
                logger.LogWarning("[{Name}] código {Code} repetido no DP {DpId} — mantido só o primeiro",
                    config.DeviceName, dp.Code, dpId);
                continue;
            }

            resolved.Add(new ResolvedEntity(dpId, dp, definition));
        }
        return resolved;
    }

    private string UpdateState(
        string deviceId, List<ResolvedEntity> entities, IReadOnlyDictionary<string, object> dps)
    {
        var cache = _state.GetOrAdd(deviceId, _ => new ConcurrentDictionary<string, object?>());

        foreach (var entity in entities)
            if (dps.TryGetValue(entity.DpId, out var raw))
                cache[entity.Dp.Code] = ToStateValue(entity.Definition, entity.Dp, raw);

        // Ordem das entidades da config; código nunca lido sai como null (unknown no HA).
        var snapshot = entities.ToDictionary(
            e => e.Dp.Code, e => cache.TryGetValue(e.Dp.Code, out var v) ? v : null);
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    private static object? ToStateValue(HaEntityDefinition definition, TuyaDpConfig dp, object raw)
    {
        if (definition.Component is HaComponent.BinarySensor or HaComponent.Switch)
            return raw switch
            {
                bool b   => b,
                double d => d != 0, // bitmaps como "fault": qualquer bit ligado
                string s => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s is "1"
                            || s.Equals(PayloadOn, StringComparison.OrdinalIgnoreCase),
                _        => null
            };

        // Enum mapeado: o valor bruto pode vir como string ("memory") ou número (0, 1, 2).
        if (definition.ValueMap is not null)
        {
            var key = raw is double n ? n.ToString(CultureInfo.InvariantCulture) : raw.ToString() ?? "";
            return definition.ValueMap.TryGetValue(key, out var label) ? label : key;
        }

        if (raw is not double number) return raw;

        var scale = dp.Scale is > 0 ? dp.Scale.Value : definition.Scale;
        return Math.Round(number * scale, ValueDecimals);
    }

    // -------------------------------------------------------------------------
    // MQTT Discovery
    // -------------------------------------------------------------------------

    private async Task PublishDiscoveryAsync(TuyaDeviceConfig config, List<ResolvedEntity> entities, CancellationToken ct)
    {
        var device = new Dictionary<string, object>
        {
            ["identifiers"]  = new[] { $"tuya_{config.DeviceId}" },
            ["name"]         = config.DeviceName,
            ["model"]        = config.DeviceModel,
            ["manufacturer"] = "Tuya"
        };

        foreach (var entity in entities)
        {
            var payload = BuildDiscoveryPayload(config, entity, device);
            await mqttService.PublishAsync(
                DiscoveryTopic(entity.Definition.Component, config.DeviceId, entity.Dp.Code),
                JsonSerializer.Serialize(payload, JsonOptions), true, ct);

            // Versões anteriores anunciavam switches como binary_sensor: config vazia remove a entidade antiga.
            if (entity.Definition.Component == HaComponent.Switch)
                await mqttService.PublishAsync(
                    DiscoveryTopic(HaComponent.BinarySensor, config.DeviceId, entity.Dp.Code), "", true, ct);
        }

        logger.LogInformation(
            "Discovery HA publicado para [{Name}] ({Id}): {Codes}",
            config.DeviceName, config.DeviceId, string.Join(", ", entities.Select(e => e.Dp.Code)));
    }

    private Dictionary<string, object> BuildDiscoveryPayload(
        TuyaDeviceConfig config, ResolvedEntity entity, Dictionary<string, object> device)
    {
        var def  = entity.Definition;
        var code = entity.Dp.Code;

        var payload = new Dictionary<string, object>
        {
            ["name"]            = string.IsNullOrWhiteSpace(entity.Dp.Name) ? def.Name : entity.Dp.Name,
            ["unique_id"]       = $"tuya_{config.DeviceId}_{code}",
            ["has_entity_name"] = true,
            ["state_topic"]     = StateTopic(config.DeviceId),
            ["value_template"]  = ValueTemplate(def.Component, code),
            ["device"]          = device,
            ["origin"]          = new Dictionary<string, string> { ["name"] = "TuyaLocalConnect" }
        };

        if (def.Component == HaComponent.Switch)
        {
            payload["command_topic"] = CommandTopic(config.DeviceId, code);
            payload["payload_on"]    = PayloadOn;
            payload["payload_off"]   = PayloadOff;
            payload["state_on"]      = PayloadOn;
            payload["state_off"]     = PayloadOff;
        }
        // expire_after só existe em sensor e binary_sensor.
        else if (Ha.ExpireAfterSeconds > 0)
        {
            payload["expire_after"] = Ha.ExpireAfterSeconds;
        }

        if (!string.IsNullOrWhiteSpace(brokerSettings?.AvailabilityTopic))
            payload["availability_topic"] = brokerSettings.AvailabilityTopic;

        if (def.DeviceClass is not null)    payload["device_class"]        = def.DeviceClass;
        if (def.Unit is not null)           payload["unit_of_measurement"] = def.Unit;
        if (def.StateClass is not null)     payload["state_class"]         = def.StateClass;
        if (def.Icon is not null)           payload["icon"]                = def.Icon;
        if (def.EntityCategory is not null) payload["entity_category"]     = def.EntityCategory;

        return payload;
    }

    /// <summary>
    /// Valor null ou ausente vira <c>None</c>, que o HA trata como unknown — em vez de OFF
    /// num binário ou erro de valor não numérico num sensor.
    /// </summary>
    private static string ValueTemplate(HaComponent component, string code)
    {
        var value = $"value_json.get('{code}')";
        return component == HaComponent.Sensor
            ? $"{{{{ {value} }}}}"
            : $"{{{{ 'None' if {value} is none else ('{PayloadOn}' if {value} else '{PayloadOff}') }}}}";
    }

    private string DiscoveryTopic(HaComponent component, string deviceId, string code)
    {
        var platform = component switch
        {
            HaComponent.BinarySensor => "binary_sensor",
            HaComponent.Switch       => "switch",
            _                        => "sensor"
        };
        return $"{Ha.DiscoveryPrefix}/{platform}/{deviceId}/{code}/config";
    }

    private string StateTopic(string deviceId) => $"{Ha.StateTopicPrefix}/{deviceId}/state";
}
