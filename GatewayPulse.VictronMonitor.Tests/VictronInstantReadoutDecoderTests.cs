using System.Security.Cryptography;
using GatewayPulse.VictronMonitor.Protocol;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class VictronInstantReadoutDecoderTests
{
    [Fact]
    public void DecodeBatteryProtect_ReferencePacket_ReturnsPublishedTelemetry()
    {
        var packet = Convert.FromHexString("1080b0a3093523fadedea38b1af8bcbde91ca8b6dbb60e");
        var key = Convert.FromHexString("fac570d66380b797a5b7543758be00e4");

        var result = VictronInstantReadoutDecoder.DecodeBatteryProtect(packet, key);

        Assert.Equal(0xA3B0, result.ProductId);
        Assert.Equal("Smart BatteryProtect 12/24V-65A", result.Model);
        Assert.Equal(13.07, result.InputVoltage);
        Assert.Equal(13.07, result.OutputVoltage);
        Assert.True(result.OutputEnabled);
        Assert.False(result.Alarm);
        Assert.Equal("Active", result.DeviceState);
        Assert.Equal("No alarm", result.AlarmReason);
    }

    [Fact]
    public void DecodeBatteryProtect_WrongKey_RejectsPacket()
    {
        var packet = Convert.FromHexString("1080b0a3093523fadedea38b1af8bcbde91ca8b6dbb60e");
        var wrongKey = Convert.FromHexString("00c570d66380b797a5b7543758be00e4");

        var error = Assert.Throws<InvalidDataException>(
            () => VictronInstantReadoutDecoder.DecodeBatteryProtect(packet, wrongKey));

        Assert.Contains("key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecodeBatteryProtect_KnownAndUnknownAlarmBits_PreservesBoth()
    {
        var key = Convert.FromHexString("fac570d66380b797a5b7543758be00e4");
        var plaintext = Convert.FromHexString("F90100018000401B051B0500000000");
        var packet = CreatePacket(plaintext, key);

        var result = VictronInstantReadoutDecoder.DecodeBatteryProtect(packet, key);

        Assert.Contains("Low voltage", result.AlarmReason);
        Assert.Contains("Unknown flags 0x8000", result.AlarmReason);
        Assert.Contains("Unknown flags 0x4000", result.WarningReason);
        Assert.Equal(0x8001, result.AlarmReasonMask);
        Assert.Equal(0x4000, result.WarningReasonMask);
    }

    [Fact]
    public void DecodeBatteryProtect_OutputStateUnavailable_RemainsUnknown()
    {
        var key = Convert.FromHexString("fac570d66380b797a5b7543758be00e4");
        var plaintext = Convert.FromHexString("F9FFFF00000000FF7FFFFF00000000");
        var packet = CreatePacket(plaintext, key);

        var result = VictronInstantReadoutDecoder.DecodeBatteryProtect(packet, key);

        Assert.Null(result.OutputEnabled);
        Assert.Equal("Not available", result.OutputState);
    }

    private static byte[] CreatePacket(byte[] plaintext, byte[] key)
    {
        var packet = Convert.FromHexString("1080B0A3093523FA000000000000000000000000000000");
        Span<byte> counter = stackalloc byte[16];
        counter[0] = 0x35;
        counter[1] = 0x23;
        Span<byte> keyStream = stackalloc byte[16];
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.EncryptEcb(counter, keyStream, PaddingMode.None);
        for (var index = 0; index < plaintext.Length; index++)
            packet[8 + index] = (byte)(plaintext[index] ^ keyStream[index]);
        return packet;
    }
}
