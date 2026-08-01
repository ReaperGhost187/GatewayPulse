using GatewayPulse.PowerMonitoring;
using GatewayPulse.VictronMonitor.Bluetooth;
using GatewayPulse.VictronMonitor.Providers;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class VictronPowerManagerTests
{
    [Fact]
    public async Task SharedScanner_RoutesBothDevicesAndIgnoresDuplicateAdvertisements()
    {
        var now = new DateTimeOffset(2026, 7, 31, 6, 0, 0, TimeSpan.Zero);
        var source = new FakeAdvertisementSource();
        var batteryProtect = new FakePowerProvider(PowerDeviceTypes.BatteryProtect, "D5:11:30:C1:55:16", now);
        var smartShunt = new FakePowerProvider(PowerDeviceTypes.SmartShunt, "AA:BB:CC:DD:EE:FF", now);
        using var manager = new VictronPowerManager(
            source,
            [batteryProtect, smartShunt],
            TimeSpan.FromSeconds(30),
            new PowerThresholds(),
            () => now);

        await manager.ConnectAsync();
        var shuntAdvertisement = Advertisement("AA:BB:CC:DD:EE:FF", 0x22);
        source.Raise(shuntAdvertisement);
        source.Raise(shuntAdvertisement);
        source.Raise(Advertisement("D5:11:30:C1:55:16", 0x33));
        var telemetry = await manager.GetTelemetryAsync();

        Assert.Equal(1, source.StartCount);
        Assert.Equal(1, smartShunt.DecodeCount);
        Assert.Equal(1, batteryProtect.DecodeCount);
        Assert.Equal(2, telemetry.Devices.Count(device => device.Connected));
        Assert.Equal(13.5m, telemetry.System!.Voltage);
        Assert.Equal(-2m, telemetry.System.Current);
        Assert.Contains(telemetry.Events, item => item.Detail == "SmartShunt connected");
        Assert.Contains(telemetry.Events, item => item.Detail == "BatteryProtect connected");
    }

    [Fact]
    public async Task OneProviderFailure_DoesNotPreventOtherProviderFromUpdating()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new FakeAdvertisementSource();
        var failing = new FakePowerProvider(PowerDeviceTypes.SmartShunt, "AA:BB:CC:DD:EE:FF", now)
        {
            DecodeError = new InvalidDataException("synthetic malformed advertisement")
        };
        var working = new FakePowerProvider(PowerDeviceTypes.BatteryProtect, "D5:11:30:C1:55:16", now);
        using var manager = new VictronPowerManager(
            source,
            [failing, working],
            TimeSpan.FromSeconds(30),
            new PowerThresholds(),
            () => now);
        await manager.ConnectAsync();

        source.Raise(Advertisement(failing.Address, 0x44));
        source.Raise(Advertisement(working.Address, 0x55));
        var result = await manager.GetTelemetryAsync();

        var shunt = Assert.Single(result.Devices, device => device.Type == PowerDeviceTypes.SmartShunt);
        Assert.False(shunt.Connected);
        Assert.Contains("ignored", shunt.Error, StringComparison.OrdinalIgnoreCase);
        var batteryProtect = Assert.Single(result.Devices, device => device.Type == PowerDeviceTypes.BatteryProtect);
        Assert.True(batteryProtect.Connected);
        Assert.True(result.Connected);
    }

    [Fact]
    public async Task StaleSmartShunt_DoesNotDisconnectFreshBatteryProtect()
    {
        var now = new DateTimeOffset(2026, 7, 31, 6, 0, 0, TimeSpan.Zero);
        var source = new FakeAdvertisementSource();
        var batteryProtect = new FakePowerProvider(PowerDeviceTypes.BatteryProtect, "D5:11:30:C1:55:16", now);
        var smartShunt = new FakePowerProvider(PowerDeviceTypes.SmartShunt, "AA:BB:CC:DD:EE:FF", now);
        using var manager = new VictronPowerManager(
            source,
            [batteryProtect, smartShunt],
            TimeSpan.FromSeconds(30),
            new PowerThresholds(),
            () => now);
        await manager.ConnectAsync();
        source.Raise(Advertisement(batteryProtect.Address, 0x66));
        source.Raise(Advertisement(smartShunt.Address, 0x77));

        now += TimeSpan.FromSeconds(31);
        batteryProtect.Timestamp = now;
        source.Raise(Advertisement(batteryProtect.Address, 0x78));
        var result = await manager.GetTelemetryAsync();

        Assert.True(Assert.Single(result.Devices, device => device.Type == PowerDeviceTypes.BatteryProtect).Connected);
        var shunt = Assert.Single(result.Devices, device => device.Type == PowerDeviceTypes.SmartShunt);
        Assert.False(shunt.Connected);
        Assert.True(shunt.Stale);
        Assert.Equal(PowerConnectionStates.Stale, shunt.ConnectionState);
        Assert.Equal("Critical", result.System!.Status);
        Assert.Contains(result.Events, item => item.Detail == "SmartShunt telemetry became stale");
    }

    [Fact]
    public async Task StaleDevice_RecoversWhenTheSamePayloadReturnsAfterBluetoothLoss()
    {
        var now = new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
        var source = new FakeAdvertisementSource();
        var smartShunt = new FakePowerProvider(PowerDeviceTypes.SmartShunt, "AA:BB:CC:DD:EE:FF", now);
        using var manager = new VictronPowerManager(
            source,
            [smartShunt],
            TimeSpan.FromSeconds(30),
            new PowerThresholds(),
            () => now);
        await manager.ConnectAsync();
        var packet = Advertisement(smartShunt.Address, 0x42);
        source.Raise(packet);

        now += TimeSpan.FromSeconds(31);
        var stale = await manager.GetTelemetryAsync();
        Assert.True(stale.Devices.Single().Stale);

        now += TimeSpan.FromSeconds(2);
        smartShunt.Timestamp = now;
        source.Raise(packet);
        var recovered = await manager.GetTelemetryAsync();

        Assert.True(recovered.Devices.Single().Connected);
        Assert.False(recovered.Devices.Single().Stale);
        Assert.Contains(recovered.Events, item => item.Detail == "SmartShunt telemetry recovered");
    }

    [Fact]
    public async Task NoUsableProviders_PublishesConfigurationStateWithoutStartingBleScanner()
    {
        var source = new FakeAdvertisementSource();
        using var manager = new VictronPowerManager(
            source,
            [],
            TimeSpan.FromSeconds(30),
            new PowerThresholds(),
            unavailableDevices:
            [
                new PowerDeviceTelemetry
                {
                    Type = PowerDeviceTypes.SmartShunt,
                    Provider = "victron-smartshunt",
                    ConnectionState = PowerConnectionStates.Misconfigured,
                    Device = "SmartShunt",
                    Error = "SmartShunt configuration is incomplete."
                }
            ]);

        Assert.True(await manager.ConnectAsync());
        var result = await manager.GetTelemetryAsync();

        Assert.Equal(0, source.StartCount);
        Assert.Single(result.Devices);
        Assert.Equal(PowerConnectionStates.Misconfigured, result.Devices[0].ConnectionState);
    }

    [Fact]
    public async Task ConfiguredSmartShuntNeverSeen_BecomesCriticalDisconnectedAfterTimeout()
    {
        var now = new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.Zero);
        var source = new FakeAdvertisementSource();
        var smartShunt = new FakePowerProvider(PowerDeviceTypes.SmartShunt, "AA:BB:CC:DD:EE:FF", now);
        using var manager = new VictronPowerManager(
            source,
            [smartShunt],
            TimeSpan.FromSeconds(30),
            new PowerThresholds(),
            () => now);
        await manager.ConnectAsync();

        now += TimeSpan.FromSeconds(31);
        var result = await manager.GetTelemetryAsync();
        var device = Assert.Single(result.Devices);

        Assert.False(device.Connected);
        Assert.False(device.Stale);
        Assert.True(device.DisconnectedBeyondTimeout);
        Assert.Equal(PowerConnectionStates.Disconnected, device.ConnectionState);
        Assert.Equal("Critical", result.System!.Status);
        Assert.Contains(result.Events, item => item.Detail == "SmartShunt disconnected");
    }

    private static VictronAdvertisement Advertisement(string address, byte discriminator) =>
        new(address, null, -60, [0x10, 0x02, discriminator]);

    private sealed class FakeAdvertisementSource : IVictronAdvertisementSource
    {
        public event EventHandler<VictronAdvertisement>? AdvertisementReceived;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public Task StartAsync() { StartCount++; return Task.CompletedTask; }
        public Task StopAsync() { StopCount++; return Task.CompletedTask; }
        public void Raise(VictronAdvertisement advertisement) => AdvertisementReceived?.Invoke(this, advertisement);
    }

    private sealed class FakePowerProvider(string type, string address, DateTimeOffset timestamp) : IPowerProvider
    {
        public string DeviceType => type;
        public string Address => address;
        public int DecodeCount { get; private set; }
        public Exception? DecodeError { get; init; }
        public DateTimeOffset Timestamp { get; set; } = timestamp;

        public PowerDeviceTelemetry Decode(VictronAdvertisement advertisement)
        {
            DecodeCount++;
            if (DecodeError is not null)
                throw DecodeError;
            return new PowerDeviceTelemetry
            {
                Type = type,
                Provider = type == PowerDeviceTypes.SmartShunt ? "victron-smartshunt" : "victron-batteryprotect",
                Connected = true,
                ConnectionState = PowerConnectionStates.Connected,
                Device = type,
                DeviceId = address,
                Voltage = type == PowerDeviceTypes.SmartShunt ? 13.5m : 13.6m,
                Current = type == PowerDeviceTypes.SmartShunt ? -2m : null,
                StateOfCharge = type == PowerDeviceTypes.SmartShunt ? 90m : null,
                OutputEnabled = type == PowerDeviceTypes.BatteryProtect ? true : null,
                Alarm = false,
                Rssi = advertisement.Rssi,
                LastUpdate = Timestamp
            };
        }

        public void Dispose() { }
    }
}
