using CamposDev.Tuya;
using CamposDEV.Mqtt.Services;
using CamposDEV.Mqtt.Settings;
using Microsoft.Extensions.Options;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

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
