namespace GatewayPulse.PowerMonitoring;

public static class PowerDeviceTypes
{
    public const string BatteryProtect = "BatteryProtect";
    public const string SmartShunt = "SmartShunt";
}

public static class PowerConnectionStates
{
    public const string Connected = "Connected";
    public const string Stale = "Stale";
    public const string Disconnected = "Disconnected";
    public const string Disabled = "Disabled";
    public const string Misconfigured = "Misconfigured";
}

public static class PowerStates
{
    public const string Charging = "Charging";
    public const string Discharging = "Discharging";
    public const string Idle = "Idle";
    public const string Unknown = "Unknown";
}

public sealed class PowerThresholds
{
    public decimal StateOfChargeWarningPercent { get; set; } = 30m;
    public decimal StateOfChargeCriticalPercent { get; set; } = 15m;
    public int WeakSignalRssi { get; set; } = -85;
    public decimal IdleCurrentAmps { get; set; } = 0.2m;
    public decimal LowVoltageWarning { get; set; } = 11.8m;
    public decimal LowVoltageCritical { get; set; } = 11.0m;
    public decimal HighVoltageWarning { get; set; } = 15.0m;
}

public sealed class PowerDeviceTelemetry
{
    public string Type { get; set; } = "Unknown";
    public string Provider { get; set; } = "unknown";
    public bool Connected { get; set; }
    public bool Stale { get; set; }
    public bool DisconnectedBeyondTimeout { get; set; }
    public string ConnectionState { get; set; } = PowerConnectionStates.Disconnected;
    public string Device { get; set; } = "Unknown power device";
    public string? DeviceId { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public decimal? Voltage { get; set; }
    public decimal? Current { get; set; }
    public decimal? Watts { get; set; }
    public decimal? StateOfCharge { get; set; }
    public decimal? ConsumedAmpHours { get; set; }
    public int? TimeRemainingMinutes { get; set; }
    public decimal? AuxiliaryInputValue { get; set; }
    public string? AuxiliaryInputType { get; set; }
    public decimal? MidpointVoltage { get; set; }
    public decimal? StarterBatteryVoltage { get; set; }
    public decimal? TemperatureCelsius { get; set; }
    public bool? OutputEnabled { get; set; }
    public bool? Alarm { get; set; }
    public string? AlarmReason { get; set; }
    public string? Firmware { get; set; }
    public int? Rssi { get; set; }
    public DateTimeOffset? LastUpdate { get; set; }
    public string? Error { get; set; }
}

public sealed class PowerSystemTelemetry
{
    public string Status { get; set; } = "Unavailable";
    public decimal? Voltage { get; set; }
    public decimal? Current { get; set; }
    public decimal? Watts { get; set; }
    public decimal? StateOfCharge { get; set; }
    public decimal? ConsumedAmpHours { get; set; }
    public int? TimeRemainingMinutes { get; set; }
    public string PowerState { get; set; } = PowerStates.Unknown;
    public bool? OutputEnabled { get; set; }
    public bool Alarm { get; set; }
    public string? AlarmReason { get; set; }
}

public sealed class PowerEvent
{
    public DateTimeOffset Timestamp { get; set; }
    public string Source { get; set; } = "Power";
    public string Type { get; set; } = "Status";
    public string Detail { get; set; } = "";
}

public static class PowerSystemComposer
{
    public static PowerTelemetry Compose(
        IEnumerable<PowerDeviceTelemetry> devices,
        DateTimeOffset updatedAt,
        PowerThresholds? thresholds = null,
        IEnumerable<PowerEvent>? events = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        thresholds ??= new PowerThresholds();
        var snapshots = devices.ToList();
        var connected = snapshots.Where(device => device.Connected && !device.Stale).ToList();
        var shunt = connected.FirstOrDefault(device =>
            device.Type.Equals(PowerDeviceTypes.SmartShunt, StringComparison.OrdinalIgnoreCase));
        var batteryProtect = connected.FirstOrDefault(device =>
            device.Type.Equals(PowerDeviceTypes.BatteryProtect, StringComparison.OrdinalIgnoreCase));
        var measurementSource = shunt ?? batteryProtect ?? connected.FirstOrDefault();
        var current = shunt?.Current;
        var watts = shunt?.Watts ??
            (shunt?.Voltage is decimal voltage && current is decimal amps ? voltage * amps : null);
        var powerState = current switch
        {
            null => PowerStates.Unknown,
            var value when value > thresholds.IdleCurrentAmps => PowerStates.Charging,
            var value when value < -thresholds.IdleCurrentAmps => PowerStates.Discharging,
            _ => PowerStates.Idle
        };
        var outputEnabled = batteryProtect?.OutputEnabled;
        var alarmReasons = connected
            .Where(device => device.Alarm == true && !string.IsNullOrWhiteSpace(device.AlarmReason))
            .Select(device => device.AlarmReason!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var alarm = connected.Any(device => device.Alarm == true);
        var stateOfCharge = shunt?.StateOfCharge;
        var status = DetermineStatus(snapshots, measurementSource?.Voltage, stateOfCharge, outputEnabled, alarm, thresholds);
        var lastUpdate = connected
            .Where(device => device.LastUpdate.HasValue)
            .Select(device => device.LastUpdate!.Value)
            .DefaultIfEmpty(updatedAt)
            .Max();

        return new PowerTelemetry
        {
            SchemaVersion = 2,
            UpdatedAt = updatedAt,
            Connected = connected.Count > 0,
            Provider = snapshots.Count > 1 ? "victron-multi-device" : measurementSource?.Provider ?? "victron-multi-device",
            Device = measurementSource?.Device ?? "Power System",
            DeviceId = measurementSource?.DeviceId,
            Model = measurementSource?.Model,
            Voltage = measurementSource?.Voltage,
            Current = current,
            StateOfCharge = stateOfCharge,
            EstimatedRuntimeMinutes = shunt?.TimeRemainingMinutes,
            OutputEnabled = outputEnabled,
            Alarm = alarm,
            AlarmReason = alarmReasons.Count == 0 ? null : string.Join(", ", alarmReasons),
            Rssi = measurementSource?.Rssi,
            LastUpdate = lastUpdate,
            Error = status == "Healthy" ? null : $"Power system status is {status}.",
            System = new PowerSystemTelemetry
            {
                Status = status,
                Voltage = measurementSource?.Voltage,
                Current = current,
                Watts = watts,
                StateOfCharge = stateOfCharge,
                ConsumedAmpHours = shunt?.ConsumedAmpHours,
                TimeRemainingMinutes = shunt?.TimeRemainingMinutes,
                PowerState = powerState,
                OutputEnabled = outputEnabled,
                Alarm = alarm,
                AlarmReason = alarmReasons.Count == 0 ? null : string.Join(", ", alarmReasons)
            },
            Devices = snapshots,
            Events = events?.OrderByDescending(item => item.Timestamp).ToList() ?? []
        };
    }

    private static string DetermineStatus(
        IReadOnlyCollection<PowerDeviceTelemetry> devices,
        decimal? voltage,
        decimal? stateOfCharge,
        bool? outputEnabled,
        bool alarm,
        PowerThresholds thresholds)
    {
        if (alarm || outputEnabled == false || stateOfCharge <= thresholds.StateOfChargeCriticalPercent)
            return "Critical";
        if (devices.Any(device =>
                device.Type.Equals(PowerDeviceTypes.SmartShunt, StringComparison.OrdinalIgnoreCase) &&
                (device.Stale || device.DisconnectedBeyondTimeout)))
            return "Critical";
        if (devices.Count == 0)
            return "Unavailable";
        if (!devices.Any(device => device.Connected && !device.Stale))
            return "Warning";
        if (devices.Any(device => device.Stale || !device.Connected) ||
            devices.Any(device => device.Rssi <= thresholds.WeakSignalRssi) ||
            stateOfCharge <= thresholds.StateOfChargeWarningPercent)
            return "Warning";
        if (!stateOfCharge.HasValue && voltage.HasValue &&
            (voltage < thresholds.LowVoltageWarning || voltage > thresholds.HighVoltageWarning))
            return voltage < thresholds.LowVoltageCritical ? "Critical" : "Warning";
        return "Healthy";
    }
}
