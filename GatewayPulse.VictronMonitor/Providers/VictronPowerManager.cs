using GatewayPulse.PowerMonitoring;
using GatewayPulse.VictronMonitor.Bluetooth;

namespace GatewayPulse.VictronMonitor.Providers;

public sealed class VictronPowerManager : IPowerMonitor, IDisposable
{
    private const int MaximumEvents = 50;
    private readonly IVictronAdvertisementSource _source;
    private readonly Dictionary<string, IPowerProvider> _providers;
    private readonly Dictionary<string, PowerDeviceTelemetry> _devices;
    private readonly Dictionary<string, DateTimeOffset> _configuredAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _lastPackets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastPacketTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PowerEvent> _events = [];
    private readonly TimeSpan _staleAfter;
    private readonly PowerThresholds _thresholds;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _sync = new();
    private bool _monitoring;
    private bool _scannerStarted;
    private bool _disposed;
    private string? _lastPowerState;
    private string? _lastBatteryLevel;

    public VictronPowerManager(
        IVictronAdvertisementSource source,
        IEnumerable<IPowerProvider> providers,
        TimeSpan staleAfter,
        PowerThresholds thresholds,
        Func<DateTimeOffset>? utcNow = null,
        IEnumerable<PowerDeviceTelemetry>? unavailableDevices = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(thresholds);
        if (staleAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(staleAfter));

        _source = source;
        _staleAfter = staleAfter;
        _thresholds = thresholds;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _providers = new Dictionary<string, IPowerProvider>(StringComparer.OrdinalIgnoreCase);
        _devices = new Dictionary<string, PowerDeviceTelemetry>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            var address = NormalizeAddress(provider.Address);
            if (!_providers.TryAdd(address, provider))
                throw new ArgumentException("Each enabled Victron device must have a unique Bluetooth address.", nameof(providers));
            _devices[address] = new PowerDeviceTelemetry
            {
                Type = provider.DeviceType,
                Provider = ProviderName(provider.DeviceType),
                Connected = false,
                ConnectionState = PowerConnectionStates.Disconnected,
                Device = provider.DeviceType,
                DeviceId = FormatAddress(address),
                Error = "Waiting for a valid Instant Readout advertisement."
            };
            _configuredAt[address] = _utcNow();
        }
        foreach (var device in unavailableDevices ?? [])
        {
            var key = string.IsNullOrWhiteSpace(device.DeviceId)
                ? $"unavailable:{_devices.Count}:{device.Type}"
                : NormalizeAddress(device.DeviceId);
            _devices[key] = Clone(device);
        }
    }

    public bool IsConnected
    {
        get
        {
            lock (_sync)
                return _devices.Values.Any(device => device.Connected && !device.Stale);
        }
    }

    public string DeviceName => "Victron Power System";

    public async Task<bool> ConnectAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_monitoring)
                return true;
            if (_providers.Count == 0)
            {
                _monitoring = true;
                return true;
            }
            _source.AdvertisementReceived += OnAdvertisementReceived;
            try
            {
                await _source.StartAsync();
                _scannerStarted = true;
                _monitoring = true;
                return true;
            }
            catch
            {
                _source.AdvertisementReceived -= OnAdvertisementReceived;
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public Task<PowerTelemetry> GetTelemetryAsync()
    {
        lock (_sync)
        {
            var now = _utcNow();
            foreach (var entry in _devices.ToArray())
            {
                var device = entry.Value;
                if (!device.Connected && !device.Stale && !device.DisconnectedBeyondTimeout &&
                    device.ConnectionState == PowerConnectionStates.Disconnected &&
                    _configuredAt.TryGetValue(entry.Key, out var configuredAt) &&
                    now - configuredAt > _staleAfter)
                {
                    var timedOut = Clone(device);
                    timedOut.DisconnectedBeyondTimeout = true;
                    timedOut.Error = "No valid Instant Readout advertisement was received before the connection timeout.";
                    _devices[entry.Key] = timedOut;
                    AddEvent(now, timedOut.Type, $"{timedOut.Type} disconnected");
                    continue;
                }
                if (!device.Connected || !device.LastUpdate.HasValue || now - device.LastUpdate.Value <= _staleAfter)
                    continue;
                var stale = Clone(device);
                stale.Connected = false;
                stale.Stale = true;
                stale.ConnectionState = PowerConnectionStates.Stale;
                stale.Error = "Instant Readout telemetry is stale; waiting to recover.";
                TrackDeviceTransitions(device, stale, now);
                _devices[entry.Key] = stale;
            }

            var result = PowerSystemComposer.Compose(
                _devices.Values.Select(Clone),
                now,
                _thresholds,
                _events.Select(Clone));
            TrackSystemTransitions(result, now);
            result.Events = _events.Select(Clone).ToList();
            return Task.FromResult(result);
        }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (!_monitoring)
                return;
            if (_scannerStarted)
            {
                _source.AdvertisementReceived -= OnAdvertisementReceived;
                await _source.StopAsync();
                _scannerStarted = false;
            }
            _monitoring = false;
            lock (_sync)
            {
                foreach (var key in _devices.Keys.ToArray())
                {
                    var stopped = Clone(_devices[key]);
                    stopped.Connected = false;
                    stopped.Stale = false;
                    stopped.ConnectionState = PowerConnectionStates.Disconnected;
                    stopped.Error = "Victron power monitor stopped.";
                    _devices[key] = stopped;
                }
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Dispose()
    {
        _lifecycle.Wait();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _source.AdvertisementReceived -= OnAdvertisementReceived;
            if (_monitoring)
            {
                if (_scannerStarted)
                {
                    _source.StopAsync().GetAwaiter().GetResult();
                    _scannerStarted = false;
                }
                _monitoring = false;
            }
            foreach (var provider in _providers.Values)
                provider.Dispose();
        }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
        }
    }

    private void OnAdvertisementReceived(object? sender, VictronAdvertisement advertisement)
    {
        var address = NormalizeAddress(advertisement.Address);
        if (!_providers.TryGetValue(address, out var provider))
            return;

        lock (_sync)
        {
            if (_disposed)
                return;
            var receivedAt = _utcNow();
            if (_lastPackets.TryGetValue(address, out var previousPacket) &&
                previousPacket.AsSpan().SequenceEqual(advertisement.ManufacturerData) &&
                _lastPacketTimes.TryGetValue(address, out var previousPacketTime) &&
                receivedAt - previousPacketTime < TimeSpan.FromSeconds(1))
            {
                return;
            }
            _lastPackets[address] = advertisement.ManufacturerData.ToArray();
            _lastPacketTimes[address] = receivedAt;

            var old = _devices[address];
            try
            {
                var current = provider.Decode(advertisement);
                current.Type = provider.DeviceType;
                current.DeviceId = FormatAddress(address);
                current.Connected = true;
                current.Stale = false;
                current.DisconnectedBeyondTimeout = false;
                current.ConnectionState = PowerConnectionStates.Connected;
                current.Error = null;
                TrackDeviceTransitions(old, current, receivedAt);
                _devices[address] = Clone(current);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                var failed = Clone(old);
                failed.Error = "Malformed or undecryptable Instant Readout advertisement was ignored.";
                failed.Rssi = advertisement.Rssi;
                _devices[address] = failed;
            }
        }
    }

    private void TrackDeviceTransitions(PowerDeviceTelemetry previous, PowerDeviceTelemetry current, DateTimeOffset now)
    {
        if (!previous.Connected && current.Connected)
        {
            AddEvent(now, current.Type, $"{current.Type} connected");
            if (previous.Stale || previous.DisconnectedBeyondTimeout)
                AddEvent(now, current.Type, $"{current.Type} telemetry recovered");
        }
        else if (previous.Connected && !current.Connected)
        {
            AddEvent(now, current.Type, $"{current.Type} disconnected");
            if (current.Stale)
                AddEvent(now, current.Type, $"{current.Type} telemetry became stale");
        }

        if (previous.OutputEnabled.HasValue && previous.OutputEnabled != current.OutputEnabled && current.OutputEnabled.HasValue)
            AddEvent(now, current.Type, current.OutputEnabled.Value
                ? "BatteryProtect output restored"
                : "BatteryProtect output disabled");
        if (previous.Alarm.HasValue && previous.Alarm != current.Alarm && current.Alarm.HasValue)
            AddEvent(now, current.Type, current.Alarm.Value ? "Power alarm raised" : "Power alarm cleared");
    }

    private void TrackSystemTransitions(PowerTelemetry telemetry, DateTimeOffset now)
    {
        var powerState = telemetry.System?.PowerState;
        if (powerState is not null && powerState != PowerStates.Unknown && powerState != _lastPowerState)
        {
            AddEvent(now, "PowerSystem", powerState switch
            {
                PowerStates.Charging => "Charging started",
                PowerStates.Discharging => "Discharging started",
                _ => "Battery became idle"
            });
            _lastPowerState = powerState;
        }

        var stateOfCharge = telemetry.System?.StateOfCharge;
        var batteryLevel = stateOfCharge switch
        {
            null => "Unknown",
            var value when value <= _thresholds.StateOfChargeCriticalPercent => "Critical",
            var value when value <= _thresholds.StateOfChargeWarningPercent => "Warning",
            _ => "Normal"
        };
        if (_lastBatteryLevel is not null && batteryLevel != _lastBatteryLevel)
        {
            if (batteryLevel == "Warning")
                AddEvent(now, "PowerSystem", "Low battery warning");
            else if (batteryLevel == "Critical")
                AddEvent(now, "PowerSystem", "Critical battery warning");
        }
        _lastBatteryLevel = batteryLevel;
    }

    private void AddEvent(DateTimeOffset timestamp, string source, string detail)
    {
        _events.Insert(0, new PowerEvent
        {
            Timestamp = timestamp,
            Source = source,
            Type = "Power",
            Detail = detail
        });
        if (_events.Count > MaximumEvents)
            _events.RemoveRange(MaximumEvents, _events.Count - MaximumEvents);
    }

    private static string ProviderName(string type) =>
        type.Equals(PowerDeviceTypes.SmartShunt, StringComparison.OrdinalIgnoreCase)
            ? "victron-smartshunt"
            : "victron-batteryprotect";

    internal static string NormalizeAddress(string address) =>
        new string(address.Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();

    private static string FormatAddress(string address) =>
        address.Length == 12
            ? string.Join(':', Enumerable.Range(0, 6).Select(index => address.Substring(index * 2, 2)))
            : address;

    private static PowerEvent Clone(PowerEvent source) => new()
    {
        Timestamp = source.Timestamp,
        Source = source.Source,
        Type = source.Type,
        Detail = source.Detail
    };

    private static PowerDeviceTelemetry Clone(PowerDeviceTelemetry source) => new()
    {
        Type = source.Type,
        Provider = source.Provider,
        Connected = source.Connected,
        Stale = source.Stale,
        DisconnectedBeyondTimeout = source.DisconnectedBeyondTimeout,
        ConnectionState = source.ConnectionState,
        Device = source.Device,
        DeviceId = source.DeviceId,
        Model = source.Model,
        SerialNumber = source.SerialNumber,
        Voltage = source.Voltage,
        Current = source.Current,
        Watts = source.Watts,
        StateOfCharge = source.StateOfCharge,
        ConsumedAmpHours = source.ConsumedAmpHours,
        TimeRemainingMinutes = source.TimeRemainingMinutes,
        AuxiliaryInputValue = source.AuxiliaryInputValue,
        AuxiliaryInputType = source.AuxiliaryInputType,
        MidpointVoltage = source.MidpointVoltage,
        StarterBatteryVoltage = source.StarterBatteryVoltage,
        TemperatureCelsius = source.TemperatureCelsius,
        OutputEnabled = source.OutputEnabled,
        Alarm = source.Alarm,
        AlarmReason = source.AlarmReason,
        Firmware = source.Firmware,
        Rssi = source.Rssi,
        LastUpdate = source.LastUpdate,
        Error = source.Error
    };
}
