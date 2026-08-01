using GatewayPulse.PowerMonitoring;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class JsonFilePowerMonitorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveStaleWindow(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new JsonFilePowerMonitor("PowerTelemetry.json", staleAfter: TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task GetTelemetryAsync_ReadsProviderNeutralTelemetryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gateway-pulse-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "PowerTelemetry.json");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(path, """
                {
                  "connected": true,
                  "provider": "mock",
                  "device": "Bench battery",
                  "voltage": 13.21,
                  "outputEnabled": true,
                  "alarm": false,
                  "lastUpdate": "2026-07-30T22:15:00Z"
                }
                """);

            IPowerMonitor monitor = new JsonFilePowerMonitor(
                path,
                staleAfter: TimeSpan.MaxValue,
                allowMockProvider: true);

            Assert.True(await monitor.ConnectAsync());
            var result = await monitor.GetTelemetryAsync();

            Assert.True(monitor.IsConnected);
            Assert.Equal("Bench battery", monitor.DeviceName);
            Assert.Equal("mock", result.Provider);
            Assert.Equal(13.21m, result.Voltage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GetTelemetryAsync_MissingFile_ReturnsDisconnectedTelemetry()
    {
        IPowerMonitor monitor = new JsonFilePowerMonitor(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        var result = await monitor.GetTelemetryAsync();

        Assert.False(result.Connected);
        Assert.False(monitor.IsConnected);
        Assert.Contains("not found", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetTelemetryAsync_RejectsMockProviderWhenDemoModeOff()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gateway-pulse-mock-reject-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            {
              "connected": true,
              "provider": "mock",
              "device": "Bench battery",
              "voltage": 13.21,
              "lastUpdate": "2026-07-30T22:15:00Z"
            }
            """);

        try
        {
            var monitor = new JsonFilePowerMonitor(path, staleAfter: TimeSpan.MaxValue, allowMockProvider: false);
            var result = await monitor.GetTelemetryAsync();

            Assert.False(result.Connected);
            Assert.Contains("Demo Mode", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task GetTelemetryAsync_StaleConnectedFile_IsReportedDisconnected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gateway-pulse-stale-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            { "connected": true, "device": "Battery", "lastUpdate": "2026-07-30T22:15:00Z" }
            """);

        try
        {
            var monitor = new JsonFilePowerMonitor(
                path,
                () => new DateTimeOffset(2026, 7, 30, 22, 16, 0, TimeSpan.Zero),
                TimeSpan.FromSeconds(30));

            var result = await monitor.GetTelemetryAsync();

            Assert.False(result.Connected);
            Assert.Contains("stale", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task GetTelemetryAsync_ConnectedFileWithoutLastUpdate_IsReportedDisconnected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gateway-pulse-missing-time-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{ "connected": true, "device": "Battery" }""");

        try
        {
            var monitor = new JsonFilePowerMonitor(
                path,
                () => new DateTimeOffset(2026, 7, 30, 22, 16, 0, TimeSpan.Zero),
                TimeSpan.FromSeconds(30));

            var result = await monitor.GetTelemetryAsync();

            Assert.False(result.Connected);
            Assert.Contains("lastUpdate", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task GetTelemetryAsync_FutureConnectedFile_IsReportedDisconnected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gateway-pulse-future-time-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{ "connected": true, "device": "Battery", "lastUpdate": "2026-07-30T22:20:00Z" }""");

        try
        {
            var monitor = new JsonFilePowerMonitor(
                path,
                () => new DateTimeOffset(2026, 7, 30, 22, 16, 0, TimeSpan.Zero),
                TimeSpan.FromSeconds(30));

            var result = await monitor.GetTelemetryAsync();

            Assert.False(result.Connected);
            Assert.Contains("future", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task GetTelemetryAsync_OneStaleDevice_DoesNotDisconnectFreshDevice()
    {
        var now = new DateTimeOffset(2026, 7, 31, 5, 0, 0, TimeSpan.Zero);
        var path = Path.Combine(Path.GetTempPath(), $"gateway-pulse-multi-stale-{Guid.NewGuid():N}.json");
        var telemetry = PowerSystemComposer.Compose(
            [
                new PowerDeviceTelemetry
                {
                    Type = PowerDeviceTypes.BatteryProtect,
                    Connected = true,
                    ConnectionState = PowerConnectionStates.Connected,
                    Device = "BatteryProtect",
                    OutputEnabled = true,
                    LastUpdate = now - TimeSpan.FromSeconds(5)
                },
                new PowerDeviceTelemetry
                {
                    Type = PowerDeviceTypes.SmartShunt,
                    Connected = true,
                    ConnectionState = PowerConnectionStates.Connected,
                    Device = "SmartShunt",
                    Voltage = 13.5m,
                    Current = -2m,
                    LastUpdate = now - TimeSpan.FromMinutes(2)
                }
            ],
            now - TimeSpan.FromSeconds(5));
        await File.WriteAllTextAsync(path, PowerTelemetryJson.Serialize(telemetry));

        try
        {
            var monitor = new JsonFilePowerMonitor(path, () => now, TimeSpan.FromSeconds(30));

            var result = await monitor.GetTelemetryAsync();

            Assert.True(result.Connected);
            Assert.Equal(2, result.SchemaVersion);
            var batteryProtect = Assert.Single(result.Devices, device => device.Type == PowerDeviceTypes.BatteryProtect);
            Assert.True(batteryProtect.Connected);
            Assert.False(batteryProtect.Stale);
            var smartShunt = Assert.Single(result.Devices, device => device.Type == PowerDeviceTypes.SmartShunt);
            Assert.False(smartShunt.Connected);
            Assert.True(smartShunt.Stale);
            Assert.Equal(PowerConnectionStates.Stale, smartShunt.ConnectionState);
            Assert.Equal("Critical", result.System!.Status);
            Assert.Equal("BatteryProtect", result.Device);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
