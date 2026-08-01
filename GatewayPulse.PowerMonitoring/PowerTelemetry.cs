namespace GatewayPulse.PowerMonitoring;

public sealed class PowerTelemetry
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset? UpdatedAt { get; set; }
    public bool Connected { get; set; }
    public string Provider { get; set; } = "unknown";
    public string Device { get; set; } = "Unknown power device";
    public string? DeviceId { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public decimal? Voltage { get; set; }
    public decimal? Current { get; set; }
    public decimal? StateOfCharge { get; set; }
    public int? EstimatedRuntimeMinutes { get; set; }
    public bool? OutputEnabled { get; set; }
    public bool? Alarm { get; set; }
    public string? AlarmReason { get; set; }
    public string? Firmware { get; set; }
    public int? Rssi { get; set; }
    public DateTimeOffset LastUpdate { get; set; } = DateTimeOffset.UtcNow;
    public string? Error { get; set; }
    public PowerSystemTelemetry? System { get; set; }
    public List<PowerDeviceTelemetry> Devices { get; set; } = [];
    public List<PowerEvent> Events { get; set; } = [];
}
