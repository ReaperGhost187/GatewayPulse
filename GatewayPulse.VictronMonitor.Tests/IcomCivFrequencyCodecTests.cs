using GatewayPulse.Core;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class IcomCivFrequencyCodecTests
{
    [Theory]
    [InlineData("94", 0x94)]
    [InlineData("0x94", 0x94)]
    [InlineData("E0", 0xE0)]
    public void TryParseAddress_AcceptsHex(string input, byte expected)
    {
        Assert.True(IcomCivFrequencyCodec.TryParseAddress(input, out var address));
        Assert.Equal(expected, address);
    }

    [Fact]
    public void DecodeBcdFrequencyHz_Decodes7140000()
    {
        // 7.140 MHz → BCD little-endian nibbles: 00 00 14 07 00
        var data = new byte[] { 0x00, 0x00, 0x14, 0x07, 0x00 };
        Assert.Equal(7_140_000, IcomCivFrequencyCodec.DecodeBcdFrequencyHz(data));
    }

    [Fact]
    public void TryDecodeFrequencyHz_FindsResponseFrame()
    {
        var frame = new byte[]
        {
            0xFE, 0xFE, 0xE0, 0x94, 0x03,
            0x00, 0x00, 0x14, 0x07, 0x00,
            0xFD
        };
        Assert.True(IcomCivFrequencyCodec.TryDecodeFrequencyHz(frame, 0x94, out var hz));
        Assert.Equal(7_140_000, hz);
    }

    [Fact]
    public void BuildReadFrequencyRequest_IsStandardCiv()
    {
        var req = IcomCivFrequencyCodec.BuildReadFrequencyRequest(0x94);
        Assert.Equal(new byte[] { 0xFE, 0xFE, 0x94, 0xE0, 0x03, 0xFD }, req);
    }
}
