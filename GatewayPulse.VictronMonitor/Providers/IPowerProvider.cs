using GatewayPulse.PowerMonitoring;
using GatewayPulse.VictronMonitor.Bluetooth;

namespace GatewayPulse.VictronMonitor.Providers;

public interface IPowerProvider : IDisposable
{
    string DeviceType { get; }
    string Address { get; }
    PowerDeviceTelemetry Decode(VictronAdvertisement advertisement);
}
