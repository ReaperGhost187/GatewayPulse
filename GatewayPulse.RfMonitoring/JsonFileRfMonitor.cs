namespace GatewayPulse.RfMonitoring;

public sealed class JsonFileRfMonitor : IRfMonitor
{
    private readonly string _path;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _staleAfter;
    private readonly bool _allowMockProvider;
    private RfTelemetry _last = CreateUnavailable("RF telemetry file");

    public JsonFileRfMonitor(
        string path,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? staleAfter = null,
        bool allowMockProvider = false)
    {
        _path = Path.GetFullPath(path);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _staleAfter = staleAfter ?? TimeSpan.FromSeconds(10);
        _allowMockProvider = allowMockProvider;
        if (_staleAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(staleAfter));
    }

    public bool IsConnected => _last.Connected;
    public string DeviceName => _last.Device;

    public Task<bool> ConnectAsync() =>
        GetTelemetryAsync().ContinueWith(t => t.Result.Connected, TaskScheduler.Default);

    public Task DisconnectAsync() => Task.CompletedTask;

    public async Task<RfTelemetry> GetTelemetryAsync()
    {
        if (!File.Exists(_path))
            return Set(CreateUnavailable($"RF telemetry file not found: {_path}"));

        try
        {
            var json = await File.ReadAllTextAsync(_path);
            var telemetry = RfTelemetryJson.Deserialize(json);
            if (telemetry is null)
                return Set(CreateUnavailable("RF telemetry file contained no data."));

            if (!_allowMockProvider &&
                string.Equals(telemetry.Provider, "mock", StringComparison.OrdinalIgnoreCase))
            {
                return Set(CreateUnavailable(
                    "Mock RF telemetry was rejected because Dashboard Demo Mode is off."));
            }

            var age = _utcNow() - telemetry.LastUpdate;
            if (telemetry.Connected && age > _staleAfter)
            {
                telemetry.Connected = false;
                telemetry.Stale = true;
                telemetry.ConnectionState = RfConnectionStates.Stale;
                telemetry.Error ??= $"RF telemetry is stale ({age.TotalSeconds:0}s old).";
                telemetry.ProtocolStatus = "Stale";
                ZeroLiveTransmitFields(telemetry);
            }
            else if (telemetry.Connected && telemetry.LastUpdate - _utcNow() > TimeSpan.FromSeconds(5))
            {
                telemetry.Connected = false;
                telemetry.ConnectionState = RfConnectionStates.Error;
                telemetry.Error = "RF telemetry timestamp is in the future.";
            }
            else if (telemetry.Connected)
            {
                telemetry.Stale = false;
                if (string.IsNullOrWhiteSpace(telemetry.ConnectionState))
                    telemetry.ConnectionState = RfConnectionStates.Connected;
            }

            return Set(telemetry);
        }
        catch (Exception ex)
        {
            return Set(CreateUnavailable($"Unable to read RF telemetry: {ex.Message}"));
        }
    }

    private RfTelemetry Set(RfTelemetry telemetry)
    {
        _last = telemetry;
        return telemetry;
    }

    private static void ZeroLiveTransmitFields(RfTelemetry telemetry)
    {
        telemetry.Transmitting = false;
        telemetry.ForwardPowerWatts = 0m;
        telemetry.ReflectedPowerWatts = 0m;
        telemetry.Swr = null;
        telemetry.Dbm = null;
        // Peak / last-peak history fields remain for labeled history display.
    }

    private static RfTelemetry CreateUnavailable(string error) => new()
    {
        Connected = false,
        Provider = "json-file",
        Device = "RF telemetry file",
        ConnectionState = RfConnectionStates.Disconnected,
        ProtocolStatus = "Unavailable",
        Error = error,
        UpdatedAt = DateTimeOffset.UtcNow,
        LastUpdate = DateTimeOffset.UtcNow
    };
}
