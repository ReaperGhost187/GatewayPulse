using GatewayPulse.RfMonitoring;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class SerialPortNameTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("4", "COM4")]
    [InlineData("10", "COM10")]
    [InlineData("com4", "COM4")]
    [InlineData("COM4", "COM4")]
    [InlineData(" Com7 ", "COM7")]
    [InlineData(@"\\.\COM4", "COM4")]
    [InlineData(@"\\.\com12", "COM12")]
    public void Normalize_ProducesComForm(string? input, string expected)
    {
        Assert.Equal(expected, SerialPortName.Normalize(input));
    }

    [Fact]
    public void Normalize_LeavesNonNumericNamesUnchangedAsideFromCase()
    {
        Assert.Equal("USB-SERIAL", SerialPortName.Normalize("usb-serial"));
    }
}
