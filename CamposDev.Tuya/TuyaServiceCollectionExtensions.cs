using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CamposDev.Tuya;

public static class TuyaServiceCollectionExtensions
{
    /// <summary>
    /// Registra os serviços de descoberta e leitura LAN Tuya no container de DI.
    /// Requer que <see cref="CamposDEV.Mqtt.Services.IMqttBrokerService"/> já esteja registrado.
    /// </summary>
    /// <param name="services">O <see cref="IServiceCollection"/> do host.</param>
    /// <param name="configuration">Seção de configuração contendo <c>TuyaApi</c>.</param>
    /// <param name="configSection">Nome da seção (padrão: <c>"TuyaApi"</c>).</param>
    public static IServiceCollection AddTuyaLan(
        this IServiceCollection services,
        IConfiguration configuration,
        string configSection = "TuyaApi")
    {
        var options = configuration.GetSection(configSection).Get<TuyaApiOptions>()
                      ?? throw new InvalidOperationException(
                          $"Seção de configuração '{configSection}' não encontrada ou vazia.");

        services.AddSingleton(options);
        services.AddSingleton<TuyaTokenService>();
        services.AddSingleton<TuyaLanDiscoveryService>();
        services.AddHostedService(sp => sp.GetRequiredService<TuyaLanDiscoveryService>());
        services.AddHostedService<TuyaLanListenerService>();

        return services;
    }
}
