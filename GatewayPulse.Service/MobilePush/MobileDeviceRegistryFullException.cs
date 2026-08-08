namespace GatewayPulse.ServiceHosting;

/// <summary>Thrown when registering a NEW deviceId would exceed the soft max registry size.</summary>
public sealed class MobileDeviceRegistryFullException : Exception
{
    public int MaxDevices { get; }

    public MobileDeviceRegistryFullException(int maxDevices)
        : base(
            $"Device registry is full (max {maxDevices} devices). " +
            "Unregister an unused device before adding a new one. " +
            "Existing deviceIds can still refresh their APNs tokens.")
    {
        MaxDevices = maxDevices;
    }
}
