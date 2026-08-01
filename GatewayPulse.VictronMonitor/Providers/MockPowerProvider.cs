using GatewayPulse.PowerMonitoring;

namespace GatewayPulse.VictronMonitor.Providers;

public sealed class MockPowerProvider : IPowerMonitor
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly Random _random;
    private bool _connected;
    private decimal _voltage = 13.21m;

    public MockPowerProvider(Func<DateTimeOffset>? clock = null, Random? random = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _random = random ?? new Random();
    }

    public bool IsConnected => _connected;
    public string DeviceName => "Victron Smart BatteryProtect 100A (Mock)";

    public Task<bool> ConnectAsync()
    {
        _connected = true;
        return Task.FromResult(true);
    }

    public Task<PowerTelemetry> GetTelemetryAsync()
    {
        if (!_connected)
        {
            return Task.FromResult(new PowerTelemetry
            {
                Connected = false,
                Provider = "mock",
                Device = DeviceName,
                LastUpdate = _clock(),
                Error = "Mock power monitor is disconnected."
            });
        }

        _voltage = decimal.Clamp(
            _voltage + decimal.Round((decimal)(_random.NextDouble() - 0.5) * 0.08m, 2),
            12.6m,
            13.8m);

        return Task.FromResult(new PowerTelemetry
        {
            Connected = true,
            Provider = "mock",
            Device = DeviceName,
            DeviceId = "MOCK-BP100",
            Model = "Smart BatteryProtect 12/24V-100A",
            SerialNumber = "MOCK000001",
            Voltage = _voltage,
            OutputEnabled = true,
            Alarm = false,
            AlarmReason = "No alarm",
            Firmware = "4.xx (mock)",
            Rssi = -48,
            LastUpdate = _clock()
        });
    }

    public Task DisconnectAsync()
    {
        _connected = false;
        return Task.CompletedTask;
    }
}
