namespace GatewayPulse.PowerMonitoring;

public interface IPowerMonitor
{
    bool IsConnected { get; }
    string DeviceName { get; }
    Task<bool> ConnectAsync();
    Task<PowerTelemetry> GetTelemetryAsync();
    Task DisconnectAsync();
}
