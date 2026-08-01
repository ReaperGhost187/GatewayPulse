namespace GatewayPulse.PowerMonitoring;

public sealed class JsonFilePowerMonitor : IPowerMonitor
{
    private readonly string _path;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _staleAfter;
    private readonly PowerThresholds _thresholds;
    private readonly bool _allowMockProvider;
    private PowerTelemetry _lastTelemetry = new()
    {
        Connected = false,
        Provider = "json-file",
        Device = "Power telemetry file"
    };

    public JsonFilePowerMonitor(
        string path,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? staleAfter = null,
        PowerThresholds? thresholds = null,
        bool allowMockProvider = false)
    {
        _path = Path.GetFullPath(path);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _staleAfter = staleAfter ?? TimeSpan.FromSeconds(30);
        _thresholds = thresholds ?? new PowerThresholds();
        _allowMockProvider = allowMockProvider;
        if (_staleAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(staleAfter), "The stale telemetry window must be positive.");
    }

    public bool IsConnected => _lastTelemetry.Connected;
    public string DeviceName => _lastTelemetry.Device;

    public async Task<bool> ConnectAsync()
    {
        var telemetry = await GetTelemetryAsync();
        return telemetry.Connected;
    }

    public async Task<PowerTelemetry> GetTelemetryAsync()
    {
        if (!File.Exists(_path))
        {
            return SetUnavailable($"Power telemetry file not found: {_path}");
        }

        try
        {
            var json = await File.ReadAllTextAsync(_path);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var hasLastUpdate = document.RootElement.TryGetProperty("lastUpdate", out var lastUpdateElement) &&
                lastUpdateElement.ValueKind == System.Text.Json.JsonValueKind.String &&
                lastUpdateElement.TryGetDateTimeOffset(out _);
            var telemetry = PowerTelemetryJson.Deserialize(json);
            if (telemetry is null)
                return SetUnavailable("Power telemetry file contained no data.");

            if (!_allowMockProvider &&
                string.Equals(telemetry.Provider, "mock", StringComparison.OrdinalIgnoreCase))
            {
                return SetUnavailable(
                    "Mock telemetry was rejected because Dashboard Demo Mode is off. Enable Demo Mode only for development.");
            }

            if (telemetry.SchemaVersion >= 2 && telemetry.Devices.Count > 0)
            {
                telemetry = NormalizeMultiDeviceTelemetry(telemetry);
                _lastTelemetry = telemetry;
                return telemetry;
            }

            if (telemetry.Connected && !hasLastUpdate)
            {
                telemetry.Connected = false;
                telemetry.Error = "Connected power telemetry must include a valid lastUpdate timestamp.";
            }
            else if (telemetry.Connected && telemetry.LastUpdate - _utcNow() > TimeSpan.FromSeconds(5))
            {
                telemetry.Connected = false;
                telemetry.Error = $"Power telemetry lastUpdate is in the future: {telemetry.LastUpdate:O}.";
            }
            else if (telemetry.Connected && _utcNow() - telemetry.LastUpdate > _staleAfter)
            {
                telemetry.Connected = false;
                telemetry.Error = $"Power telemetry is stale; last update was {telemetry.LastUpdate:O}.";
            }

            _lastTelemetry = telemetry;
            return telemetry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return SetUnavailable($"Unable to read power telemetry: {ex.Message}");
        }
    }

    public Task DisconnectAsync()
    {
        _lastTelemetry = new PowerTelemetry
        {
            Connected = false,
            Provider = "json-file",
            Device = _lastTelemetry.Device,
            Error = "Power telemetry reader disconnected."
        };
        return Task.CompletedTask;
    }

    private PowerTelemetry SetUnavailable(string error)
    {
        _lastTelemetry = new PowerTelemetry
        {
            Connected = false,
            Provider = "json-file",
            Device = "Power telemetry file",
            Error = error
        };
        return _lastTelemetry;
    }

    private PowerTelemetry NormalizeMultiDeviceTelemetry(PowerTelemetry telemetry)
    {
        var now = _utcNow();
        foreach (var device in telemetry.Devices)
        {
            if (!device.Connected)
            {
                if (string.IsNullOrWhiteSpace(device.ConnectionState))
                    device.ConnectionState = PowerConnectionStates.Disconnected;
                continue;
            }

            if (!device.LastUpdate.HasValue)
            {
                device.Connected = false;
                device.Stale = false;
                device.ConnectionState = PowerConnectionStates.Disconnected;
                device.Error = "Connected device telemetry must include a valid lastUpdate timestamp.";
            }
            else if (device.LastUpdate.Value - now > TimeSpan.FromSeconds(5))
            {
                device.Connected = false;
                device.Stale = false;
                device.ConnectionState = PowerConnectionStates.Disconnected;
                device.Error = $"Device telemetry lastUpdate is in the future: {device.LastUpdate:O}.";
            }
            else if (now - device.LastUpdate.Value > _staleAfter)
            {
                device.Connected = false;
                device.Stale = true;
                device.ConnectionState = PowerConnectionStates.Stale;
                device.Error = $"Device telemetry is stale; last update was {device.LastUpdate:O}.";
            }
            else
            {
                device.Stale = false;
                device.DisconnectedBeyondTimeout = false;
                device.ConnectionState = PowerConnectionStates.Connected;
            }
        }

        return PowerSystemComposer.Compose(
            telemetry.Devices,
            telemetry.UpdatedAt ?? telemetry.LastUpdate,
            _thresholds,
            events: telemetry.Events);
    }
}
