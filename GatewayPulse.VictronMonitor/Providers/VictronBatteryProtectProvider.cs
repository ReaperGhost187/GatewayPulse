using GatewayPulse.PowerMonitoring;
using GatewayPulse.VictronMonitor.Bluetooth;
using GatewayPulse.VictronMonitor.Protocol;
using System.Security.Cryptography;

namespace GatewayPulse.VictronMonitor.Providers;

public sealed class VictronBatteryProtectProvider : IPowerMonitor, IDisposable
{
    private readonly IVictronAdvertisementSource _source;
    private readonly byte[] _advertisementKey;
    private readonly string? _targetAddress;
    private readonly string? _targetName;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _staleAfter;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _decodeSync = new();
    private bool _monitoring;
    private bool _disposed;
    private PowerTelemetry _telemetry = new()
    {
        Connected = false,
        Provider = "victron-batteryprotect",
        Device = "Victron Smart BatteryProtect"
    };

    public VictronBatteryProtectProvider(
        IVictronAdvertisementSource source,
        byte[] advertisementKey,
        string? targetAddress = null,
        string? targetName = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? staleAfter = null)
    {
        if (advertisementKey.Length != 16)
            throw new ArgumentException("The Victron advertisement key must contain exactly 16 bytes.", nameof(advertisementKey));
        if (string.IsNullOrWhiteSpace(targetAddress) && string.IsNullOrWhiteSpace(targetName))
            throw new ArgumentException("A target Bluetooth address or device-name filter is required.");

        _source = source;
        _advertisementKey = advertisementKey.ToArray();
        _targetAddress = NormalizeAddress(targetAddress);
        _targetName = string.IsNullOrWhiteSpace(targetName) ? null : targetName.Trim();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _staleAfter = staleAfter ?? TimeSpan.FromSeconds(30);
    }

    public bool IsConnected
    {
        get
        {
            lock (_sync)
                return _telemetry.Connected && _utcNow() - _telemetry.LastUpdate <= _staleAfter;
        }
    }

    public string DeviceName
    {
        get
        {
            lock (_sync)
                return _telemetry.Device;
        }
    }

    public async Task<bool> ConnectAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_monitoring)
                return true;

            _source.AdvertisementReceived += OnAdvertisementReceived;
            try
            {
                await _source.StartAsync();
                _monitoring = true;
            }
            catch
            {
                _source.AdvertisementReceived -= OnAdvertisementReceived;
                throw;
            }
            return true;
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
            if (_telemetry.Connected && !IsConnected)
            {
                _telemetry = CloneDisconnected(_telemetry, "Victron advertisements are stale; waiting to reconnect.");
            }

            return Task.FromResult(Clone(_telemetry));
        }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_monitoring)
            {
                _source.AdvertisementReceived -= OnAdvertisementReceived;
                await _source.StopAsync();
                _monitoring = false;
            }

            lock (_sync)
                _telemetry = CloneDisconnected(_telemetry, "Victron monitor stopped.");
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
            try
            {
                if (_monitoring)
                {
                    _source.StopAsync().GetAwaiter().GetResult();
                    _monitoring = false;
                }
            }
            finally
            {
                lock (_decodeSync)
                    CryptographicOperations.ZeroMemory(_advertisementKey);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void OnAdvertisementReceived(object? sender, VictronAdvertisement advertisement)
    {
        if (!MatchesTarget(advertisement))
            return;

        lock (_decodeSync)
        {
            if (_disposed)
                return;

            try
            {
                var readout = VictronInstantReadoutDecoder.DecodeBatteryProtect(
                    advertisement.ManufacturerData,
                    _advertisementKey);
                var now = _utcNow();
                lock (_sync)
                {
                    _telemetry = new PowerTelemetry
                    {
                        Connected = true,
                        Provider = "victron-batteryprotect",
                        Device = string.IsNullOrWhiteSpace(advertisement.Name) ? readout.Model : advertisement.Name,
                        DeviceId = advertisement.Address,
                        Model = readout.Model,
                        Voltage = readout.InputVoltage.HasValue ? (decimal)readout.InputVoltage.Value : null,
                        OutputEnabled = readout.OutputEnabled,
                        Alarm = readout.Alarm,
                        AlarmReason = BuildAlarmReason(readout),
                        Rssi = advertisement.Rssi,
                        LastUpdate = now
                    };
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or CryptographicException)
            {
                lock (_sync)
                {
                    var error = $"Unable to decode Victron advertisement: {ex.Message}";
                    if (_telemetry.Connected)
                    {
                        _telemetry = Clone(_telemetry);
                        _telemetry.Error = error;
                    }
                    else
                    {
                        _telemetry = CloneDisconnected(_telemetry, error);
                        _telemetry.LastUpdate = _utcNow();
                        _telemetry.Rssi = advertisement.Rssi;
                        _telemetry.DeviceId = advertisement.Address;
                    }
                }
            }
        }
    }

    private bool MatchesTarget(VictronAdvertisement advertisement)
    {
        if (_targetAddress is not null && NormalizeAddress(advertisement.Address) != _targetAddress)
            return false;
        if (_targetName is not null && !(advertisement.Name?.Contains(_targetName, StringComparison.OrdinalIgnoreCase) ?? false))
            return false;
        return true;
    }

    private static string BuildAlarmReason(BatteryProtectReadout readout)
    {
        var reasons = new List<string>();
        if (!readout.AlarmReason.Equals("No alarm", StringComparison.OrdinalIgnoreCase))
            reasons.Add(readout.AlarmReason);
        if (readout.ErrorCode is not null && !readout.ErrorCode.Equals("No error", StringComparison.OrdinalIgnoreCase))
            reasons.Add(readout.ErrorCode);
        if (!readout.WarningReason.Equals("No alarm", StringComparison.OrdinalIgnoreCase))
            reasons.Add($"Warning: {readout.WarningReason}");
        return reasons.Count == 0 ? "No alarm" : string.Join(", ", reasons);
    }

    private static string? NormalizeAddress(string? address) =>
        string.IsNullOrWhiteSpace(address)
            ? null
            : new string(address.Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();

    private static PowerTelemetry CloneDisconnected(PowerTelemetry source, string error) => new()
    {
        Connected = false,
        Provider = source.Provider,
        Device = source.Device,
        DeviceId = source.DeviceId,
        Model = source.Model,
        SerialNumber = source.SerialNumber,
        Voltage = source.Voltage,
        OutputEnabled = source.OutputEnabled,
        Alarm = source.Alarm,
        AlarmReason = source.AlarmReason,
        Firmware = source.Firmware,
        Rssi = source.Rssi,
        LastUpdate = source.LastUpdate,
        Error = error
    };

    private static PowerTelemetry Clone(PowerTelemetry source) => new()
    {
        Connected = source.Connected,
        Provider = source.Provider,
        Device = source.Device,
        DeviceId = source.DeviceId,
        Model = source.Model,
        SerialNumber = source.SerialNumber,
        Voltage = source.Voltage,
        Current = source.Current,
        StateOfCharge = source.StateOfCharge,
        EstimatedRuntimeMinutes = source.EstimatedRuntimeMinutes,
        OutputEnabled = source.OutputEnabled,
        Alarm = source.Alarm,
        AlarmReason = source.AlarmReason,
        Firmware = source.Firmware,
        Rssi = source.Rssi,
        LastUpdate = source.LastUpdate,
        Error = source.Error
    };
}
