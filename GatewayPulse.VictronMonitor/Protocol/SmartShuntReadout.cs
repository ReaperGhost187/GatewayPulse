namespace GatewayPulse.VictronMonitor.Protocol;

public sealed class SmartShuntReadout
{
    public ushort ProductId { get; init; }
    public string Model { get; init; } = "Unknown Victron SmartShunt";
    public double? Voltage { get; init; }
    public double? Current { get; init; }
    public double? ConsumedAmpHours { get; init; }
    public double? StateOfCharge { get; init; }
    public int? TimeRemainingMinutes { get; init; }
    public bool Alarm { get; init; }
    public ushort AlarmReasonMask { get; init; }
    public string AlarmReason { get; init; } = "No alarm";
    public double? AuxiliaryInputValue { get; init; }
    public string AuxiliaryInputType { get; init; } = "Disabled";
    public double? StarterBatteryVoltage { get; init; }
    public double? MidpointVoltage { get; init; }
    public double? TemperatureCelsius { get; init; }
}
