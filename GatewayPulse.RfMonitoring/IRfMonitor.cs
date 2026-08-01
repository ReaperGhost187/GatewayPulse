namespace GatewayPulse.RfMonitoring;

public interface IRfMonitor
{
    bool IsConnected { get; }
    string DeviceName { get; }
    Task<bool> ConnectAsync();
    Task<RfTelemetry> GetTelemetryAsync();
    Task DisconnectAsync();
}
