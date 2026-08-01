using GatewayPulse.PowerMonitoring;
using GatewayPulse.VictronMonitor.Providers;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class MockPowerProviderTests
{
    [Fact]
    public async Task ConnectedProvider_ReturnsRealisticBatteryProtectTelemetry()
    {
        IPowerMonitor provider = new MockPowerProvider(
            () => new DateTimeOffset(2026, 7, 30, 22, 15, 0, TimeSpan.Zero),
            new Random(1234));

        Assert.True(await provider.ConnectAsync());
        var telemetry = await provider.GetTelemetryAsync();

        Assert.True(provider.IsConnected);
        Assert.True(telemetry.Connected);
        Assert.Equal("mock", telemetry.Provider);
        Assert.Contains("BatteryProtect", provider.DeviceName);
        Assert.InRange(telemetry.Voltage!.Value, 12.6m, 13.8m);
        Assert.True(telemetry.OutputEnabled);
        Assert.False(telemetry.Alarm);
        Assert.Equal(new DateTimeOffset(2026, 7, 30, 22, 15, 0, TimeSpan.Zero), telemetry.LastUpdate);
    }

    [Fact]
    public async Task DisconnectedProvider_ReturnsDisconnectedTelemetry()
    {
        IPowerMonitor provider = new MockPowerProvider();
        await provider.ConnectAsync();
        await provider.DisconnectAsync();

        var telemetry = await provider.GetTelemetryAsync();

        Assert.False(provider.IsConnected);
        Assert.False(telemetry.Connected);
    }
}
