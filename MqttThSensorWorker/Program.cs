using CamposDev.Tuya;
using CamposDEV.Mqtt.Services;
using CamposDEV.Mqtt.Settings;
using Microsoft.Extensions.Options;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// LocalKeys dos devices fora do git: appsettings.local.json é ignorado pelo .gitignore e entra
// na imagem no build. Variáveis de ambiente e argumentos são adicionados de novo depois dele
// para continuarem vencendo (ex.: TuyaApi__Devices__0__LocalKey no compose ou no Kubernetes).
builder.Configuration
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

builder.Services.AddSerilog((_, config) =>
    config.WriteTo.Console()
          .ReadFrom.Configuration(builder.Configuration));

builder.Services.AddOptions<MqttBrokerSettings>()
    .Bind(builder.Configuration.GetSection("MqttBrokerSettings"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<MqttBrokerSettings>>().Value);
builder.Services.AddSingleton<IMqttBrokerService, MqttBrokerService>();

builder.Services.AddTuyaLan(builder.Configuration);

var host = builder.Build();

// Inicializa o MQTT antes de iniciar o host
var mqtt = host.Services.GetRequiredService<IMqttBrokerService>();
await mqtt.InitializeAsync();

host.Run();
