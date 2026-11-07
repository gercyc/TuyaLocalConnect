namespace CamposDev.Tuya;

/// <summary>Plataforma de entidade do Home Assistant usada no MQTT Discovery.</summary>
internal enum HaComponent
{
    Sensor,
    BinarySensor,
    Switch
}

/// <summary>
/// Como um código de DP Tuya vira entidade no Home Assistant.
/// </summary>
/// <param name="Component">Plataforma HA (<c>sensor</c>, <c>binary_sensor</c> ou <c>switch</c>).</param>
/// <param name="Name">Nome padrão da entidade, o mesmo usado pelo localtuya.</param>
/// <param name="DeviceClass">device_class do HA, se houver.</param>
/// <param name="Unit">unit_of_measurement, se houver.</param>
/// <param name="StateClass">state_class (<c>measurement</c>, <c>total_increasing</c>).</param>
/// <param name="Scale">Fator aplicado ao valor bruto numérico do DP.</param>
/// <param name="EntityCategory">entity_category do HA (<c>config</c> ou <c>diagnostic</c>), se houver.</param>
/// <param name="Icon">Ícone mdi, se houver.</param>
/// <param name="ValueMap">Tradução de valores enum (valor bruto → rótulo).</param>
internal sealed record HaEntityDefinition(
    HaComponent                          Component,
    string                               Name,
    string?                              DeviceClass  = null,
    string?                              Unit         = null,
    string?                              StateClass   = null,
    double                               Scale        = 1,
    string?                              EntityCategory = null,
    string?                              Icon         = null,
    IReadOnlyDictionary<string, string>? ValueMap     = null);

/// <summary>
/// Catálogo código de DP → entidade HA, no padrão das tabelas <c>kg</c>, <c>cz</c> e <c>pc</c>
/// do localtuya (core/ha_entities). Nomes, device_class, unidades e escalas padrão vêm de lá.
/// </summary>
/// <remarks>
/// Os <c>switch</c> são controláveis pelo HA (command_topic). O que no localtuya é <c>select</c>
/// ou <c>number</c> aqui ainda é <c>sensor</c> somente leitura, com entity_category
/// <c>diagnostic</c> no lugar de <c>config</c>, a única categoria que o HA aceita em sensores.
/// </remarks>
internal static class HaEntityCatalog
{
    private const int MaxGangs = 8;

    private const string Config     = "config";
    private const string Diagnostic = "diagnostic";

    /// <summary>Categorias de tomada (<c>cz</c>, <c>pc</c>): o localtuya usa device_class outlet.</summary>
    private static readonly HashSet<string> OutletCategories = new(StringComparer.OrdinalIgnoreCase) { "cz", "pc" };

    private static readonly IReadOnlyDictionary<string, string> RelayStatusMap = new Dictionary<string, string>
    {
        ["on"]        = "ON",
        ["off"]       = "OFF",
        ["memory"]    = "Last State",
        ["power_on"]  = "ON",
        ["power_off"] = "OFF",
        ["last"]      = "Last State",
        ["0"]         = "ON",
        ["1"]         = "OFF",
        ["2"]         = "Last State"
    };

    private static readonly IReadOnlyDictionary<string, string> LightModeMap = new Dictionary<string, string>
    {
        ["relay"] = "State",
        ["pos"]   = "Position",
        ["none"]  = "OFF"
    };

    private static readonly IReadOnlyDictionary<string, HaEntityDefinition> Definitions = BuildDefinitions();

    /// <summary>
    /// Retorna a definição para o código, ajustada à categoria do device, ou null se o código
    /// não vira entidade (ex.: coeficientes de calibração, strings de agendamento).
    /// </summary>
    public static HaEntityDefinition? Find(string code, string? category)
    {
        if (!Definitions.TryGetValue(code, out var definition)) return null;

        var isOutletSwitch = definition.Component == HaComponent.Switch
                             && definition.DeviceClass == "switch"
                             && category is not null
                             && OutletCategories.Contains(category);

        return isOutletSwitch ? definition with { DeviceClass = "outlet" } : definition;
    }

    private static Dictionary<string, HaEntityDefinition> BuildDefinitions()
    {
        var defs = new Dictionary<string, HaEntityDefinition>(StringComparer.Ordinal)
        {
            // Liga/desliga ------------------------------------------------------
            ["switch"] = new(HaComponent.Switch, "Switch", DeviceClass: "switch"),

            // Configuração (switch/select/number no localtuya) ------------------
            ["child_lock"]        = new(HaComponent.Switch, "Child Lock", EntityCategory: Config, Icon: "mdi:account-lock"),
            ["overcharge_switch"] = new(HaComponent.Switch, "Overcharge", EntityCategory: Config, Icon: "mdi:flash-alert"),
            ["switch_backlight"]  = new(HaComponent.Switch, "State light", EntityCategory: Config, Icon: "mdi:lightbulb-outline"),
            ["relay_status"]      = new(HaComponent.Sensor, "Power-on behavior", EntityCategory: Diagnostic, Icon: "mdi:circle-double", ValueMap: RelayStatusMap),
            ["light_mode"]        = new(HaComponent.Sensor, "Light Mode", EntityCategory: Diagnostic, ValueMap: LightModeMap),
            ["fault"]             = new(HaComponent.BinarySensor, "Fault", DeviceClass: "problem", EntityCategory: Diagnostic),

            // Medição de energia (SENSORS["kg"], reaproveitado por cz/pc) -------
            ["cur_current"] = new(HaComponent.Sensor, "Current", "current", "mA", "measurement"),
            ["cur_power"]   = new(HaComponent.Sensor, "Power", "power", "W", "measurement", Scale: 0.1),
            ["cur_voltage"] = new(HaComponent.Sensor, "Voltage", "voltage", "V", "measurement", Scale: 0.1),
            ["add_ele"]     = new(HaComponent.Sensor, "Electricity", "energy", "kWh", "total_increasing", Scale: 0.001),

            // Sensores a bateria (mcs e afins) ------------------------------------
            ["doorcontact_state"]  = new(HaComponent.BinarySensor, "Door", DeviceClass: "door"),
            ["battery_percentage"] = new(HaComponent.Sensor, "Battery", "battery", "%", "measurement", EntityCategory: Diagnostic)
        };

        for (var gang = 1; gang <= MaxGangs; gang++)
        {
            defs[$"switch_{gang}"]    = new(HaComponent.Switch, $"Switch {gang}", DeviceClass: "switch");
            defs[$"countdown_{gang}"] = new(HaComponent.Sensor, $"Switch {gang} Timer", "duration", "s",
                                            EntityCategory: Diagnostic, Icon: "mdi:timer");
        }

        return defs;
    }
}
