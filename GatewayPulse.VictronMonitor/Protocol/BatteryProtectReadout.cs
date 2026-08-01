namespace GatewayPulse.VictronMonitor.Protocol;

public sealed class BatteryProtectReadout
{
    public required ushort ProductId { get; init; }
    public required string Model { get; init; }
    public required double? InputVoltage { get; init; }
    public required double? OutputVoltage { get; init; }
    public required bool? OutputEnabled { get; init; }
    public required bool Alarm { get; init; }
    public required byte DeviceStateCode { get; init; }
    public required string DeviceState { get; init; }
    public required byte OutputStateCode { get; init; }
    public required string OutputState { get; init; }
    public required byte? ErrorCodeValue { get; init; }
    public required string? ErrorCode { get; init; }
    public required ushort AlarmReasonMask { get; init; }
    public required string AlarmReason { get; init; }
    public required ushort WarningReasonMask { get; init; }
    public required string WarningReason { get; init; }
    public required uint OffReason { get; init; }
}
