using GatewayPulse.PowerMonitoring;
using GatewayPulse.VictronMonitor.Bluetooth;
using GatewayPulse.VictronMonitor.Providers;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class SmartShuntDecoderTests
{
    [Fact]
    public void Decode_NormalizesAllAvailableInstantReadoutFields()
    {
        var timestamp = new DateTimeOffset(2026, 7, 31, 7, 0, 0, TimeSpan.Zero);
        using var decoder = new SmartShuntDecoder(
            "AA:BB:CC:DD:EE:FF",
            SmartShuntInstantReadoutDecoderTests.SyntheticKey,
            () => timestamp);
        var advertisement = new VictronAdvertisement(
            "AA:BB:CC:DD:EE:FF",
            "SmartShunt HQ123",
            -61,
            SmartShuntInstantReadoutDecoderTests.CreatePacket(
                remainingMinutes: 2040,
                voltageHundredths: 1357,
                alarmMask: 1,
                auxiliaryRaw: 29815,
                auxiliaryMode: 2,
                currentMilliamps: -3800,
                consumedAmpHourTenths: 84,
                stateOfChargeTenths: 960));

        var result = decoder.Decode(advertisement);

        Assert.Equal(PowerDeviceTypes.SmartShunt, result.Type);
        Assert.Equal("victron-smartshunt", result.Provider);
        Assert.True(result.Connected);
        Assert.Equal("SmartShunt HQ123", result.Device);
        Assert.Equal("AA:BB:CC:DD:EE:FF", result.DeviceId);
        Assert.Equal("SmartShunt 300A/50mV", result.Model);
        Assert.Equal(13.57m, result.Voltage);
        Assert.Equal(-3.8m, result.Current);
        Assert.Equal(-51.566m, result.Watts);
        Assert.Equal(96m, result.StateOfCharge);
        Assert.Equal(-8.4m, result.ConsumedAmpHours);
        Assert.Equal(2040, result.TimeRemainingMinutes);
        Assert.Equal(25m, result.TemperatureCelsius);
        Assert.Equal("Temperature", result.AuxiliaryInputType);
        Assert.Equal(25m, result.AuxiliaryInputValue);
        Assert.True(result.Alarm);
        Assert.Equal("Low voltage", result.AlarmReason);
        Assert.Equal(-61, result.Rssi);
        Assert.Equal(timestamp, result.LastUpdate);
    }
}
