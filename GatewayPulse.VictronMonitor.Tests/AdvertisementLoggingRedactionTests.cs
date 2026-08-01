using GatewayPulse.VictronMonitor.Bluetooth;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class AdvertisementLoggingRedactionTests
{
    [Fact]
    public void VictronManufacturerData_RedactsKeyCheckByteWithoutMutatingDecoderInput()
    {
        byte[] input = [0x10, 0x02, 0x38, 0xC0, 0x02, 0x34, 0x12, 0xAB, 0x55];

        var logged = WindowsBleAdvertisementSource.RedactManufacturerDataForLogging(0x02E1, input);

        Assert.Equal(0xAB, input[7]);
        Assert.Equal(0, logged[7]);
        Assert.Equal(input[..7], logged[..7]);
        Assert.Equal(input[8..], logged[8..]);
    }

    [Fact]
    public void VictronAdvertisementSection_RedactsEmbeddedKeyCheckByte()
    {
        byte[] section = [0xE1, 0x02, 0x10, 0x02, 0x38, 0xC0, 0x02, 0x34, 0x12, 0xAB, 0x55];

        var logged = WindowsBleAdvertisementSource.RedactAdvertisementSectionForLogging(0xFF, section);

        Assert.Equal(0xAB, section[9]);
        Assert.Equal(0, logged[9]);
    }

    [Fact]
    public void NonVictronManufacturerData_IsNotChanged()
    {
        byte[] input = [0x10, 0x02, 0x38, 0xC0, 0x02, 0x34, 0x12, 0xAB];

        var logged = WindowsBleAdvertisementSource.RedactManufacturerDataForLogging(0x1234, input);

        Assert.Equal(input, logged);
    }
}
