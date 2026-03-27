namespace CamposDev.Tuya;

public record TuyaDevice(
    string         GwId,
    string         Ip,
    string         ProductKey,
    string         Version,
    bool           Encrypt,
    DateTimeOffset SeenAt);

public record TuyaToken(
    string         AccessToken,
    string         RefreshToken,
    DateTimeOffset ExpiresAt);

public record SensorReading(
    double         Temperature,
    double         Humidity,
    string         BatteryState,
    int            BatteryPercent,
    string         TempUnit,
    double         HeatIndex,
    string         ComfortLevel,
    DateTimeOffset MeasuredAt);
