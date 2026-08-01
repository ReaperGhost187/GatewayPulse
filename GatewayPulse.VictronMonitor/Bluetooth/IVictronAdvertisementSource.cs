namespace GatewayPulse.VictronMonitor.Bluetooth;

public sealed record VictronAdvertisement(
    string Address,
    string? Name,
    int Rssi,
    byte[] ManufacturerData,
    IReadOnlyList<Guid>? ServiceUuids = null,
    bool? IsConnectable = null,
    byte[]? RawPacket = null);

public interface IVictronAdvertisementSource
{
    event EventHandler<VictronAdvertisement>? AdvertisementReceived;
    Task StartAsync();
    Task StopAsync();
}
