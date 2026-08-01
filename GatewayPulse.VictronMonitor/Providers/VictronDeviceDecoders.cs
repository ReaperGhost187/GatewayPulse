using System.Security.Cryptography;
using GatewayPulse.PowerMonitoring;
using GatewayPulse.VictronMonitor.Bluetooth;
using GatewayPulse.VictronMonitor.Protocol;

namespace GatewayPulse.VictronMonitor.Providers;

public abstract class VictronDeviceDecoderBase : IPowerProvider
{
    private readonly byte[] _advertisementKey;
    private readonly object _sync = new();
    private bool _disposed;

    protected VictronDeviceDecoderBase(
        string address,
        byte[] advertisementKey,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (advertisementKey.Length != 16)
            throw new ArgumentException("The Victron advertisement key must contain exactly 16 bytes.", nameof(advertisementKey));
        var normalized = VictronPowerManager.NormalizeAddress(address);
        if (normalized.Length != 12 || !normalized.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A valid six-byte Bluetooth address is required.", nameof(address));
        Address = string.Join(':', Enumerable.Range(0, 6).Select(index => normalized.Substring(index * 2, 2)));
        _advertisementKey = advertisementKey.ToArray();
        UtcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public abstract string DeviceType { get; }
    public string Address { get; }
    protected Func<DateTimeOffset> UtcNow { get; }

    public PowerDeviceTelemetry Decode(VictronAdvertisement advertisement)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!VictronPowerManager.NormalizeAddress(advertisement.Address)
                    .Equals(VictronPowerManager.NormalizeAddress(Address), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The advertisement address does not match the configured device.");
            return DecodeCore(advertisement, _advertisementKey);
        }
    }

    protected abstract PowerDeviceTelemetry DecodeCore(VictronAdvertisement advertisement, byte[] advertisementKey);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            CryptographicOperations.ZeroMemory(_advertisementKey);
        }
    }
}

public sealed class SmartShuntDecoder : VictronDeviceDecoderBase
{
    public SmartShuntDecoder(string address, byte[] advertisementKey, Func<DateTimeOffset>? utcNow = null)
        : base(address, advertisementKey, utcNow) { }

    public override string DeviceType => PowerDeviceTypes.SmartShunt;

    protected override PowerDeviceTelemetry DecodeCore(VictronAdvertisement advertisement, byte[] advertisementKey)
    {
        var readout = VictronInstantReadoutDecoder.DecodeSmartShunt(advertisement.ManufacturerData, advertisementKey);
        decimal? voltage = readout.Voltage.HasValue ? (decimal)readout.Voltage.Value : null;
        decimal? current = readout.Current.HasValue ? (decimal)readout.Current.Value : null;
        return new PowerDeviceTelemetry
        {
            Type = PowerDeviceTypes.SmartShunt,
            Provider = "victron-smartshunt",
            Connected = true,
            ConnectionState = PowerConnectionStates.Connected,
            Device = string.IsNullOrWhiteSpace(advertisement.Name) ? readout.Model : advertisement.Name,
            DeviceId = Address,
            Model = readout.Model,
            Voltage = voltage,
            Current = current,
            Watts = voltage.HasValue && current.HasValue ? voltage.Value * current.Value : null,
            StateOfCharge = readout.StateOfCharge.HasValue ? (decimal)readout.StateOfCharge.Value : null,
            ConsumedAmpHours = readout.ConsumedAmpHours.HasValue ? (decimal)readout.ConsumedAmpHours.Value : null,
            TimeRemainingMinutes = readout.TimeRemainingMinutes,
            AuxiliaryInputValue = readout.AuxiliaryInputValue.HasValue ? (decimal)readout.AuxiliaryInputValue.Value : null,
            AuxiliaryInputType = readout.AuxiliaryInputType,
            MidpointVoltage = readout.MidpointVoltage.HasValue ? (decimal)readout.MidpointVoltage.Value : null,
            StarterBatteryVoltage = readout.StarterBatteryVoltage.HasValue ? (decimal)readout.StarterBatteryVoltage.Value : null,
            TemperatureCelsius = readout.TemperatureCelsius.HasValue ? (decimal)readout.TemperatureCelsius.Value : null,
            Alarm = readout.Alarm,
            AlarmReason = readout.AlarmReason,
            Rssi = advertisement.Rssi,
            LastUpdate = UtcNow()
        };
    }
}

public sealed class BatteryProtectDecoder : VictronDeviceDecoderBase
{
    public BatteryProtectDecoder(string address, byte[] advertisementKey, Func<DateTimeOffset>? utcNow = null)
        : base(address, advertisementKey, utcNow) { }

    public override string DeviceType => PowerDeviceTypes.BatteryProtect;

    protected override PowerDeviceTelemetry DecodeCore(VictronAdvertisement advertisement, byte[] advertisementKey)
    {
        var readout = VictronInstantReadoutDecoder.DecodeBatteryProtect(advertisement.ManufacturerData, advertisementKey);
        return new PowerDeviceTelemetry
        {
            Type = PowerDeviceTypes.BatteryProtect,
            Provider = "victron-batteryprotect",
            Connected = true,
            ConnectionState = PowerConnectionStates.Connected,
            Device = string.IsNullOrWhiteSpace(advertisement.Name) ? readout.Model : advertisement.Name,
            DeviceId = Address,
            Model = readout.Model,
            Voltage = readout.InputVoltage.HasValue ? (decimal)readout.InputVoltage.Value : null,
            OutputEnabled = readout.OutputEnabled,
            Alarm = readout.Alarm,
            AlarmReason = BuildAlarmReason(readout),
            Rssi = advertisement.Rssi,
            LastUpdate = UtcNow()
        };
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
}
