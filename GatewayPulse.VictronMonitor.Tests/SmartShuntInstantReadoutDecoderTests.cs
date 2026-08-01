using System.Buffers.Binary;
using System.Security.Cryptography;
using GatewayPulse.VictronMonitor.Protocol;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class SmartShuntInstantReadoutDecoderTests
{
    internal static readonly byte[] SyntheticKey = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();

    [Fact]
    public void DecodePublishedDecryptedFixture_MatchesIndependentReferenceValues()
    {
        byte[] publishedPayload =
        [
            0xFF, 0xFF, 0xE5, 0x04, 0x00, 0x00, 0x00, 0x00,
            0x03, 0x00, 0x00, 0xF4, 0x01, 0x40, 0xDF
        ];

        var result = VictronInstantReadoutDecoder.DecodeSmartShuntPayload(publishedPayload, 0xA389);

        Assert.Equal("SmartShunt 500A/50mV", result.Model);
        Assert.Null(result.TimeRemainingMinutes);
        Assert.Equal(12.53, result.Voltage);
        Assert.Equal(0, result.Current);
        Assert.Equal(-50, result.ConsumedAmpHours);
        Assert.Equal(50, result.StateOfCharge);
        Assert.Equal("Disabled", result.AuxiliaryInputType);
        Assert.Null(result.AuxiliaryInputValue);
        Assert.False(result.Alarm);
    }

    [Fact]
    public void DecodeSmartShunt_DecodesSignedCurrentAndAllCoreMeasurements()
    {
        var packet = CreatePacket(
            productId: 0xC038,
            remainingMinutes: 2040,
            voltageHundredths: 1357,
            alarmMask: 0,
            auxiliaryRaw: ushort.MaxValue,
            auxiliaryMode: 3,
            currentMilliamps: -3800,
            consumedAmpHourTenths: 84,
            stateOfChargeTenths: 960);

        var result = VictronInstantReadoutDecoder.DecodeSmartShunt(packet, SyntheticKey);

        Assert.Equal(0xC038, result.ProductId);
        Assert.Equal("SmartShunt 300A/50mV", result.Model);
        Assert.Equal(13.57, result.Voltage);
        Assert.Equal(-3.8, result.Current);
        Assert.Equal(-8.4, result.ConsumedAmpHours);
        Assert.Equal(96.0, result.StateOfCharge);
        Assert.Equal(2040, result.TimeRemainingMinutes);
        Assert.False(result.Alarm);
        Assert.Equal("No alarm", result.AlarmReason);
        Assert.Equal("Disabled", result.AuxiliaryInputType);
        Assert.Null(result.AuxiliaryInputValue);
        Assert.Null(result.StarterBatteryVoltage);
        Assert.Null(result.MidpointVoltage);
        Assert.Null(result.TemperatureCelsius);
    }

    [Theory]
    [InlineData(0, 1275, 12.75, null, null)]
    [InlineData(1, 680, null, 6.8, null)]
    [InlineData(2, 29815, null, null, 25.0)]
    public void DecodeSmartShunt_InterpretsAuxiliaryInputByConfiguredMode(
        int mode,
        int raw,
        double? expectedStarterVoltage,
        double? expectedMidpointVoltage,
        double? expectedTemperature)
    {
        var packet = CreatePacket(auxiliaryRaw: checked((ushort)raw), auxiliaryMode: mode);

        var result = VictronInstantReadoutDecoder.DecodeSmartShunt(packet, SyntheticKey);

        Assert.Equal(expectedStarterVoltage, result.StarterBatteryVoltage);
        Assert.Equal(expectedMidpointVoltage, result.MidpointVoltage);
        Assert.Equal(expectedTemperature, result.TemperatureCelsius);
    }

    [Fact]
    public void DecodeSmartShunt_MapsUnavailableSentinelsToNull()
    {
        var packet = CreatePacket(
            remainingMinutes: ushort.MaxValue,
            voltageHundredths: short.MaxValue,
            auxiliaryRaw: ushort.MaxValue,
            auxiliaryMode: 3,
            currentRawOverride: 0x3FFFFF,
            consumedAmpHourTenths: 0xFFFFF,
            stateOfChargeTenths: 0x3FF);

        var result = VictronInstantReadoutDecoder.DecodeSmartShunt(packet, SyntheticKey);

        Assert.Null(result.TimeRemainingMinutes);
        Assert.Null(result.Voltage);
        Assert.Null(result.Current);
        Assert.Null(result.ConsumedAmpHours);
        Assert.Null(result.StateOfCharge);
    }

    [Fact]
    public void DecodeSmartShunt_WrongEncryptionKeyIsRejectedWithoutLeakingKeyMaterial()
    {
        var packet = CreatePacket();
        var wrongKey = Enumerable.Range(16, 16).Select(value => (byte)value).ToArray();

        var error = Assert.Throws<InvalidDataException>(
            () => VictronInstantReadoutDecoder.DecodeSmartShunt(packet, wrongKey));

        Assert.Contains("does not match", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(wrongKey), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecodeSmartShunt_MalformedPayloadIsRejected()
    {
        Assert.Throws<InvalidDataException>(
            () => VictronInstantReadoutDecoder.DecodeSmartShunt([0x10, 0x02], SyntheticKey));
    }

    internal static byte[] CreatePacket(
        ushort productId = 0xC038,
        int remainingMinutes = 120,
        int voltageHundredths = 1280,
        ushort alarmMask = 0,
        ushort auxiliaryRaw = ushort.MaxValue,
        int auxiliaryMode = 3,
        int currentMilliamps = 0,
        int? currentRawOverride = null,
        int consumedAmpHourTenths = 0,
        int stateOfChargeTenths = 1000)
    {
        var payload = new byte[15];
        var writer = new LittleEndianBitWriter(payload);
        writer.Write((uint)remainingMinutes, 16);
        writer.Write(unchecked((ushort)voltageHundredths), 16);
        writer.Write(alarmMask, 16);
        writer.Write(auxiliaryRaw, 16);
        writer.Write((uint)auxiliaryMode, 2);
        var currentRaw = currentRawOverride ?? (currentMilliamps < 0 ? (1 << 22) + currentMilliamps : currentMilliamps);
        writer.Write((uint)currentRaw, 22);
        writer.Write((uint)consumedAmpHourTenths, 20);
        writer.Write((uint)stateOfChargeTenths, 10);

        const ushort iv = 0x1234;
        var encrypted = CryptAesCtrLittleEndian(payload, SyntheticKey, iv);
        var packet = new byte[8 + encrypted.Length];
        packet[0] = VictronInstantReadoutDecoder.InstantReadoutRecordType;
        packet[1] = 0x02;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), productId);
        packet[4] = 0x02;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5, 2), iv);
        packet[7] = SyntheticKey[0];
        encrypted.CopyTo(packet, 8);
        return packet;
    }

    private static byte[] CryptAesCtrLittleEndian(byte[] input, byte[] key, ushort initialCounter)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var encryptor = aes.CreateEncryptor();
        var counter = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(counter, initialCounter);
        var keyStream = new byte[16];
        encryptor.TransformBlock(counter, 0, counter.Length, keyStream, 0);
        return input.Select((value, index) => (byte)(value ^ keyStream[index])).ToArray();
    }

    private sealed class LittleEndianBitWriter(byte[] buffer)
    {
        private int _offset;

        public void Write(uint value, int bits)
        {
            for (var bit = 0; bit < bits; bit++, _offset++)
            {
                if ((value & (1u << bit)) != 0)
                    buffer[_offset >> 3] |= (byte)(1 << (_offset & 7));
            }
        }
    }
}
