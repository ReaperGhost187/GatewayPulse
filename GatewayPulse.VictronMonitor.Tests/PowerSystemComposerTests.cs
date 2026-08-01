using GatewayPulse.PowerMonitoring;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class PowerSystemComposerTests
{
    [Fact]
    public void Compose_BatteryProtectAndSmartShunt_UsesShuntMeasurementsAndProtectOutput()
    {
        var updatedAt = new DateTimeOffset(2026, 7, 31, 5, 0, 0, TimeSpan.Zero);
        var devices = new[]
        {
            new PowerDeviceTelemetry
            {
                Type = PowerDeviceTypes.BatteryProtect,
                Provider = "victron-batteryprotect",
                Connected = true,
                ConnectionState = PowerConnectionStates.Connected,
                Device = "BatteryProtect",
                DeviceId = "D5:11:30:C1:55:16",
                Voltage = 13.61m,
                OutputEnabled = true,
                Alarm = false,
                Rssi = -63,
                LastUpdate = updatedAt
            },
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
                LastUpdate = updatedAt
            }
        };

        var result = PowerSystemComposer.Compose(devices, updatedAt);

        Assert.Equal(2, result.SchemaVersion);
        Assert.True(result.Connected);
        Assert.NotNull(result.System);
        Assert.Equal("Healthy", result.System.Status);
        Assert.Equal(13.57m, result.System.Voltage);
        Assert.Equal(-3.8m, result.System.Current);
        Assert.Equal(-51.566m, result.System.Watts);
        Assert.Equal("Discharging", result.System.PowerState);
        Assert.Equal(96m, result.System.StateOfCharge);
        Assert.Equal(-8.4m, result.System.ConsumedAmpHours);
        Assert.Equal(2040, result.System.TimeRemainingMinutes);
        Assert.True(result.System.OutputEnabled);
        Assert.Equal(2, result.Devices.Count);

        // Version-one fields remain populated for older API/dashboard consumers.
        Assert.Equal(13.57m, result.Voltage);
        Assert.True(result.OutputEnabled);
        Assert.Equal(updatedAt, result.LastUpdate);
    }

    [Theory]
    [InlineData("0.21", "Charging")]
    [InlineData("-0.21", "Discharging")]
    [InlineData("0.20", "Idle")]
    [InlineData("-0.20", "Idle")]
    [InlineData("0", "Idle")]
    public void Compose_CurrentSignDeterminesPowerState(string currentText, string expectedState)
    {
        var current = decimal.Parse(currentText, System.Globalization.CultureInfo.InvariantCulture);
        var result = PowerSystemComposer.Compose(
            [new PowerDeviceTelemetry
            {
                Type = PowerDeviceTypes.SmartShunt,
                Connected = true,
                ConnectionState = PowerConnectionStates.Connected,
                Device = "SmartShunt",
                Voltage = 12.5m,
                Current = current,
                LastUpdate = DateTimeOffset.UtcNow
            }],
            DateTimeOffset.UtcNow);

        Assert.Equal(expectedState, result.System!.PowerState);
        Assert.Equal(12.5m * current, result.System.Watts);
    }

    [Fact]
    public void Compose_MissingCurrentLeavesWattsNull()
    {
        var result = PowerSystemComposer.Compose(
            [new PowerDeviceTelemetry
            {
                Type = PowerDeviceTypes.SmartShunt,
                Connected = true,
                ConnectionState = PowerConnectionStates.Connected,
                Device = "SmartShunt",
                Voltage = 12.5m,
                Current = null,
                LastUpdate = DateTimeOffset.UtcNow
            }],
            DateTimeOffset.UtcNow);

        Assert.Null(result.System!.Watts);
        Assert.Equal(PowerStates.Unknown, result.System.PowerState);
    }

    [Fact]
    public void Compose_BatteryProtectOnly_PreservesExistingDashboardFields()
    {
        var updatedAt = DateTimeOffset.UtcNow;
        var result = PowerSystemComposer.Compose(
            [new PowerDeviceTelemetry
            {
                Type = PowerDeviceTypes.BatteryProtect,
                Provider = "victron-batteryprotect",
                Connected = true,
                ConnectionState = PowerConnectionStates.Connected,
                Device = "BatteryProtect",
                Voltage = 13.2m,
                OutputEnabled = true,
                Alarm = false,
                Rssi = -55,
                LastUpdate = updatedAt
            }],
            updatedAt);

        Assert.True(result.Connected);
        Assert.Equal("victron-batteryprotect", result.Provider);
        Assert.Equal("BatteryProtect", result.Device);
        Assert.Equal(13.2m, result.Voltage);
        Assert.True(result.OutputEnabled);
        Assert.False(result.Alarm);
        Assert.Equal(-55, result.Rssi);
        Assert.Equal("Healthy", result.System!.Status);
    }
}
