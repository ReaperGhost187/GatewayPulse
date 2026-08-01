using GatewayPulse.VictronMonitor.Bluetooth;
using GatewayPulse.VictronMonitor.Providers;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class VictronBatteryProtectProviderTests
{
    [Fact]
    public async Task AdvertisementFromConfiguredDevice_DecodesTelemetry()
    {
        var source = new FakeAdvertisementSource();
        var key = Convert.FromHexString("fac570d66380b797a5b7543758be00e4");
        using var provider = new VictronBatteryProtectProvider(
            source,
            key,
            targetAddress: "AA:BB:CC:DD:EE:FF",
            utcNow: () => new DateTimeOffset(2026, 7, 30, 22, 15, 0, TimeSpan.Zero));
        await provider.ConnectAsync();

        source.Emit(new VictronAdvertisement(
            "AA:BB:CC:DD:EE:FF",
            "BP-65",
            -48,
            Convert.FromHexString("1080b0a3093523fadedea38b1af8bcbde91ca8b6dbb60e")));

        var telemetry = await provider.GetTelemetryAsync();

        Assert.True(provider.IsConnected);
        Assert.Equal("victron-batteryprotect", telemetry.Provider);
        Assert.Equal(13.07m, telemetry.Voltage);
        Assert.True(telemetry.OutputEnabled);
        Assert.False(telemetry.Alarm);
        Assert.Equal(-48, telemetry.Rssi);
        Assert.Equal("Smart BatteryProtect 12/24V-65A", telemetry.Model);

        telemetry.Connected = false;
        Assert.True((await provider.GetTelemetryAsync()).Connected);

        var malformed = Convert.FromHexString("1080b0a309352300dedea38b1af8bcbde91ca8b6dbb60e");
        source.Emit(new VictronAdvertisement("AA:BB:CC:DD:EE:FF", "BP-65", -49, malformed));
        var afterMalformedPacket = await provider.GetTelemetryAsync();
        Assert.True(afterMalformedPacket.Connected);
        Assert.NotNull(afterMalformedPacket.Error);
    }

    [Fact]
    public async Task AdvertisementFromDifferentAddress_IsIgnored()
    {
        var source = new FakeAdvertisementSource();
        using var provider = new VictronBatteryProtectProvider(
            source,
            Convert.FromHexString("fac570d66380b797a5b7543758be00e4"),
            targetAddress: "AA:BB:CC:DD:EE:FF");
        await provider.ConnectAsync();

        source.Emit(new VictronAdvertisement(
            "11:22:33:44:55:66",
            "Other Victron",
            -60,
            Convert.FromHexString("1080b0a3093523fadedea38b1af8bcbde91ca8b6dbb60e")));

        Assert.False((await provider.GetTelemetryAsync()).Connected);
    }

    [Fact]
    public async Task DeviceErrorWithoutAlarmFlags_PublishesMeaningfulAlarmReason()
    {
        var source = new FakeAdvertisementSource();
        using var provider = new VictronBatteryProtectProvider(
            source,
            Convert.FromHexString("fac570d66380b797a5b7543758be00e4"),
            targetAddress: "AA:BB:CC:DD:EE:FF");
        await provider.ConnectAsync();

        source.Emit(new VictronAdvertisement(
            "AA:BB:CC:DD:EE:FF",
            "BP-65",
            -48,
            Convert.FromHexString("1080b0a3093523fadedea08b1af8bcbde91ca8b6dbb60e")));

        var telemetry = await provider.GetTelemetryAsync();

        Assert.True(telemetry.Alarm);
        Assert.Contains("Error 3", telemetry.AlarmReason);
        Assert.DoesNotContain("No alarm", telemetry.AlarmReason);
    }

    [Fact]
    public async Task ConcurrentConnectCalls_StartAdvertisementSourceOnce()
    {
        var source = new FakeAdvertisementSource();
        using var provider = new VictronBatteryProtectProvider(
            source,
            Convert.FromHexString("fac570d66380b797a5b7543758be00e4"),
            targetAddress: "AA:BB:CC:DD:EE:FF");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.ConnectAsync()));

        Assert.Equal(1, source.StartCalls);
    }

    private sealed class FakeAdvertisementSource : IVictronAdvertisementSource
    {
        public int StartCalls { get; private set; }
        public event EventHandler<VictronAdvertisement>? AdvertisementReceived;
        public Task StartAsync()
        {
            StartCalls++;
            return Task.CompletedTask;
        }
        public Task StopAsync() => Task.CompletedTask;
        public void Emit(VictronAdvertisement advertisement) =>
            AdvertisementReceived?.Invoke(this, advertisement);
    }
}
