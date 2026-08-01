using System.Reflection;
using GatewayPulse.VictronMonitor.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class GattDeviceInspectorTests
{
    [Theory]
    [InlineData(GattCommunicationStatus.Success, false)]
    [InlineData(GattCommunicationStatus.Unreachable, true)]
    [InlineData(GattCommunicationStatus.ProtocolError, true)]
    [InlineData(GattCommunicationStatus.AccessDenied, true)]
    public void CharacteristicDiscoveryFailure_RequiresReconnect(
        GattCommunicationStatus status,
        bool expected)
    {
        var method = typeof(GattDeviceInspector).GetMethod(
            "RequiresReconnect",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal(expected, method.Invoke(null, [status]));
    }
}
