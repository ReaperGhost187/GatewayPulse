using System.Text.Json;
using GatewayPulse.PowerMonitoring;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class PowerTelemetryTests
{
    [Fact]
    public void JsonContract_UsesDashboardFieldNamesAndUtcTimestamp()
    {
        var telemetry = new PowerTelemetry
        {
            Connected = true,
            Device = "Victron Smart BatteryProtect 100A",
            Voltage = 13.21m,
            OutputEnabled = true,
            Alarm = false,
            Firmware = "4.xx",
            Rssi = -48,
            LastUpdate = new DateTimeOffset(2026, 7, 30, 22, 15, 0, TimeSpan.Zero)
        };

        var json = PowerTelemetryJson.Serialize(telemetry);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.GetProperty("connected").GetBoolean());
        Assert.Equal("Victron Smart BatteryProtect 100A", root.GetProperty("device").GetString());
        Assert.Equal(13.21m, root.GetProperty("voltage").GetDecimal());
        Assert.True(root.GetProperty("outputEnabled").GetBoolean());
        Assert.False(root.GetProperty("alarm").GetBoolean());
        Assert.Equal("4.xx", root.GetProperty("firmware").GetString());
        Assert.Equal(-48, root.GetProperty("rssi").GetInt32());
        Assert.Equal("2026-07-30T22:15:00+00:00", root.GetProperty("lastUpdate").GetString());
        Assert.DoesNotContain("stateOfCharge", json);
    }

    [Fact]
    public async Task ConcurrentAtomicWrites_LeaveOneValidSnapshotAndNoTemporaryFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gateway-pulse-json-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "PowerTelemetry.json");

        try
        {
            await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
                PowerTelemetryJson.WriteFileAtomicallyAsync(path, new PowerTelemetry
                {
                    Connected = true,
                    Provider = "test",
                    Device = $"Device {index}",
                    LastUpdate = DateTimeOffset.UtcNow
                })));

            Assert.NotNull(PowerTelemetryJson.Deserialize(await File.ReadAllTextAsync(path)));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SchemaV2ApiContract_SerializesNormalizedSystemAndDevicesWithoutSecretsOrNullFabrication()
    {
        var timestamp = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var telemetry = PowerSystemComposer.Compose(
        [
            new PowerDeviceTelemetry
            {
                Type = PowerDeviceTypes.SmartShunt,
                Provider = "victron-smartshunt",
                Connected = true,
                ConnectionState = PowerConnectionStates.Connected,
                Device = "SmartShunt 300A",
                DeviceId = "AA:BB:CC:DD:EE:FF",
                Voltage = 13.57m,
                Current = -3.8m,
                StateOfCharge = 96m,
                ConsumedAmpHours = -8.4m,
                TimeRemainingMinutes = 2040,
                Alarm = false,
                Rssi = -61,
                LastUpdate = timestamp
            }
        ], timestamp);

        var json = PowerTelemetryJson.Serialize(telemetry);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var system = root.GetProperty("system");
        var smartShunt = root.GetProperty("devices")[0];

        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(-3.8m, system.GetProperty("current").GetDecimal());
        Assert.Equal(-51.566m, system.GetProperty("watts").GetDecimal());
        Assert.Equal("Discharging", system.GetProperty("powerState").GetString());
        Assert.Equal("SmartShunt", smartShunt.GetProperty("type").GetString());
        Assert.False(smartShunt.TryGetProperty("temperatureCelsius", out _));
        Assert.DoesNotContain("key", json, StringComparison.OrdinalIgnoreCase);
    }
}
