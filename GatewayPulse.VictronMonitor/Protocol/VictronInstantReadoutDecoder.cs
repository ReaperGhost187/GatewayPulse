using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GatewayPulse.VictronMonitor.Protocol;

public static class VictronInstantReadoutDecoder
{
    public const ushort VictronCompanyId = 0x02E1;
    public const byte InstantReadoutRecordType = 0x10;
    public const byte SmartShuntReadoutType = 0x02;
    public const byte BatteryProtectReadoutType = 0x09;

    private static readonly IReadOnlyDictionary<ushort, string> Models =
        new Dictionary<ushort, string>
        {
            [0xA3B0] = "Smart BatteryProtect 12/24V-65A",
            [0xA3B1] = "Smart BatteryProtect 12/24V-100A",
            [0xA3B2] = "Smart BatteryProtect 12/24V-220A",
            [0xA3B3] = "Smart BatteryProtect 48V-100A",
            [0xA389] = "SmartShunt 500A/50mV",
            [0xC038] = "SmartShunt 300A/50mV"
        };

    public static SmartShuntReadout DecodeSmartShunt(
        ReadOnlySpan<byte> manufacturerData,
        byte[] advertisementKey)
    {
        if (advertisementKey.Length != 16)
            throw new ArgumentException("The Victron advertisement key must contain exactly 16 bytes.", nameof(advertisementKey));
        if (manufacturerData.Length < 23)
            throw new InvalidDataException("The Victron Instant Readout packet is too short for SmartShunt telemetry.");
        if (manufacturerData[0] != InstantReadoutRecordType)
            throw new InvalidDataException("The manufacturer data is not a Victron Instant Readout packet.");
        if (manufacturerData[4] != SmartShuntReadoutType)
            throw new InvalidDataException($"Victron readout type 0x{manufacturerData[4]:X2} is not SmartShunt data.");
        if (manufacturerData[7] != advertisementKey[0])
            throw new InvalidDataException("The advertisement key does not match the packet key-check byte.");

        var productId = BinaryPrimitives.ReadUInt16LittleEndian(manufacturerData[2..4]);
        var iv = BinaryPrimitives.ReadUInt16LittleEndian(manufacturerData[5..7]);
        var decrypted = DecryptAesCtrLittleEndian(manufacturerData.Slice(8, 15), advertisementKey, iv);
        return DecodeSmartShuntPayload(decrypted, productId);
    }

    public static SmartShuntReadout DecodeSmartShuntPayload(
        ReadOnlySpan<byte> decryptedPayload,
        ushort productId)
    {
        if (decryptedPayload.Length < 15)
            throw new InvalidDataException("The decrypted SmartShunt payload is incomplete.");
        var decrypted = decryptedPayload[..15].ToArray();
        var reader = new LittleEndianBitReader(decrypted);
        var remainingRaw = reader.ReadUnsigned(16);
        var voltageRaw = reader.ReadSigned(16);
        var alarmMask = checked((ushort)reader.ReadUnsigned(16));
        var auxiliaryRaw = checked((ushort)reader.ReadUnsigned(16));
        var auxiliaryMode = checked((byte)reader.ReadUnsigned(2));
        var currentRaw = reader.ReadUnsigned(22);
        var consumedRaw = reader.ReadUnsigned(20);
        var stateOfChargeRaw = reader.ReadUnsigned(10);

        if (voltageRaw != short.MaxValue && voltageRaw is < 0 or > 10000)
            throw new InvalidDataException($"SmartShunt battery voltage value {voltageRaw} is implausible.");
        if (stateOfChargeRaw != 0x3FF && stateOfChargeRaw > 1000)
            throw new InvalidDataException($"SmartShunt state-of-charge value {stateOfChargeRaw} is outside the published range.");

        var auxiliaryType = auxiliaryMode switch
        {
            0 => "StarterVoltage",
            1 => "MidpointVoltage",
            2 => "Temperature",
            _ => "Disabled"
        };
        double? auxiliaryValue = null;
        double? starterVoltage = null;
        double? midpointVoltage = null;
        double? temperatureCelsius = null;
        if (auxiliaryRaw != ushort.MaxValue)
        {
            switch (auxiliaryMode)
            {
                case 0:
                    starterVoltage = unchecked((short)auxiliaryRaw) / 100.0;
                    auxiliaryValue = starterVoltage;
                    break;
                case 1:
                    midpointVoltage = auxiliaryRaw / 100.0;
                    auxiliaryValue = midpointVoltage;
                    break;
                case 2:
                    temperatureCelsius = Math.Round(auxiliaryRaw / 100.0 - 273.15, 2);
                    auxiliaryValue = temperatureCelsius;
                    break;
            }
        }

        return new SmartShuntReadout
        {
            ProductId = productId,
            Model = Models.TryGetValue(productId, out var model) ? model : $"Unknown Victron product 0x{productId:X4}",
            TimeRemainingMinutes = remainingRaw == ushort.MaxValue ? null : checked((int)remainingRaw),
            Voltage = voltageRaw == short.MaxValue ? null : voltageRaw / 100.0,
            Current = currentRaw == 0x3FFFFF ? null : SignExtend(currentRaw, 22) / 1000.0,
            ConsumedAmpHours = consumedRaw == 0xFFFFF ? null : -consumedRaw / 10.0,
            StateOfCharge = stateOfChargeRaw == 0x3FF ? null : stateOfChargeRaw / 10.0,
            Alarm = alarmMask != 0,
            AlarmReasonMask = alarmMask,
            AlarmReason = AlarmReasonName(alarmMask),
            AuxiliaryInputType = auxiliaryType,
            AuxiliaryInputValue = auxiliaryValue,
            StarterBatteryVoltage = starterVoltage,
            MidpointVoltage = midpointVoltage,
            TemperatureCelsius = temperatureCelsius
        };
    }

    public static BatteryProtectReadout DecodeBatteryProtect(
        ReadOnlySpan<byte> manufacturerData,
        byte[] advertisementKey)
    {
        if (advertisementKey.Length != 16)
            throw new ArgumentException("The Victron advertisement key must contain exactly 16 bytes.", nameof(advertisementKey));
        if (manufacturerData.Length < 23)
            throw new InvalidDataException("The Victron Instant Readout packet is too short for BatteryProtect telemetry.");
        if (manufacturerData[0] != InstantReadoutRecordType)
            throw new InvalidDataException("The manufacturer data is not a Victron Instant Readout packet.");
        if (manufacturerData[4] != BatteryProtectReadoutType)
            throw new InvalidDataException($"Victron readout type 0x{manufacturerData[4]:X2} is not BatteryProtect data.");
        if (manufacturerData[7] != advertisementKey[0])
            throw new InvalidDataException("The advertisement key does not match the packet key-check byte.");

        var productId = BinaryPrimitives.ReadUInt16LittleEndian(manufacturerData[2..4]);
        var iv = BinaryPrimitives.ReadUInt16LittleEndian(manufacturerData[5..7]);
        var decrypted = DecryptAesCtrLittleEndian(manufacturerData.Slice(8, 15), advertisementKey, iv);
        if (decrypted.Length < 15)
            throw new InvalidDataException("The decrypted BatteryProtect payload is incomplete.");

        var deviceState = decrypted[0];
        var outputState = decrypted[1];
        var errorCode = decrypted[2];
        var alarmReason = BinaryPrimitives.ReadUInt16LittleEndian(decrypted.AsSpan(3, 2));
        var warningReason = BinaryPrimitives.ReadUInt16LittleEndian(decrypted.AsSpan(5, 2));
        var inputRaw = BinaryPrimitives.ReadInt16LittleEndian(decrypted.AsSpan(7, 2));
        var outputRaw = BinaryPrimitives.ReadUInt16LittleEndian(decrypted.AsSpan(9, 2));
        var offReason = BinaryPrimitives.ReadUInt32LittleEndian(decrypted.AsSpan(11, 4));
        if (outputState is not (0 or 1 or 4 or 0xFF))
            throw new InvalidDataException($"BatteryProtect output state {outputState} is outside the published range.");
        if (inputRaw != short.MaxValue && inputRaw is < 0 or > 10000)
            throw new InvalidDataException($"BatteryProtect input voltage value {inputRaw} is implausible.");
        if (outputRaw != ushort.MaxValue && outputRaw > 10000)
            throw new InvalidDataException($"BatteryProtect output voltage value {outputRaw} is implausible.");

        return new BatteryProtectReadout
        {
            ProductId = productId,
            Model = Models.TryGetValue(productId, out var model) ? model : $"Unknown Victron product 0x{productId:X4}",
            InputVoltage = inputRaw == short.MaxValue ? null : inputRaw / 100.0,
            OutputVoltage = outputRaw == ushort.MaxValue ? null : outputRaw / 100.0,
            OutputEnabled = outputState switch
            {
                1 => true,
                0 or 4 => false,
                _ => null
            },
            Alarm = alarmReason != 0 || errorCode is not (0 or 0xFF),
            DeviceStateCode = deviceState,
            DeviceState = DeviceStateName(deviceState),
            OutputStateCode = outputState,
            OutputState = OutputStateName(outputState),
            ErrorCodeValue = errorCode == 0xFF ? null : errorCode,
            ErrorCode = errorCode == 0xFF ? null : errorCode == 0 ? "No error" : $"Error {errorCode}",
            AlarmReasonMask = alarmReason,
            AlarmReason = AlarmReasonName(alarmReason),
            WarningReasonMask = warningReason,
            WarningReason = AlarmReasonName(warningReason),
            OffReason = offReason
        };
    }

    private static byte[] DecryptAesCtrLittleEndian(
        ReadOnlySpan<byte> encrypted,
        byte[] key,
        ushort initialCounter)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;

        using var encryptor = aes.CreateEncryptor();
        var counter = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(counter, initialCounter);
        var keyStream = new byte[16];
        var decrypted = new byte[encrypted.Length];

        try
        {
            for (var offset = 0; offset < encrypted.Length; offset += 16)
            {
                encryptor.TransformBlock(counter, 0, counter.Length, keyStream, 0);
                var count = Math.Min(16, encrypted.Length - offset);
                for (var index = 0; index < count; index++)
                    decrypted[offset + index] = (byte)(encrypted[offset + index] ^ keyStream[index]);
                IncrementLittleEndian(counter);
            }

            return decrypted;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyStream);
        }
    }

    private static void IncrementLittleEndian(Span<byte> counter)
    {
        for (var index = 0; index < counter.Length; index++)
        {
            counter[index]++;
            if (counter[index] != 0)
                break;
        }
    }

    private static int SignExtend(uint value, int bits) =>
        (value & (1u << (bits - 1))) == 0
            ? checked((int)value)
            : checked((int)value) - (1 << bits);

    private sealed class LittleEndianBitReader(ReadOnlyMemory<byte> data)
    {
        private int _offset;

        public uint ReadUnsigned(int bits)
        {
            if (bits is < 1 or > 32 || _offset + bits > data.Length * 8)
                throw new InvalidDataException("The decrypted SmartShunt payload is incomplete.");
            uint value = 0;
            var span = data.Span;
            for (var bit = 0; bit < bits; bit++, _offset++)
                value |= (uint)((span[_offset >> 3] >> (_offset & 7)) & 1) << bit;
            return value;
        }

        public int ReadSigned(int bits) => SignExtend(ReadUnsigned(bits), bits);
    }

    private static string DeviceStateName(byte value) => value switch
    {
        0 => "Off",
        1 => "Low power",
        2 => "Fault",
        249 => "Active",
        252 => "External control",
        255 => "Not available",
        _ => $"State {value}"
    };

    private static string OutputStateName(byte value) => value switch
    {
        0 => "Shutdown",
        1 => "On",
        4 => "Off",
        255 => "Not available",
        _ => $"Output state {value}"
    };

    private static string AlarmReasonName(ushort value)
    {
        if (value == 0)
            return "No alarm";

        var reasons = new List<string>();
        AddFlag(0x0001, "Low voltage");
        AddFlag(0x0002, "High voltage");
        AddFlag(0x0004, "Low state of charge");
        AddFlag(0x0008, "Low starter voltage");
        AddFlag(0x0010, "High starter voltage");
        AddFlag(0x0020, "Low temperature");
        AddFlag(0x0040, "High temperature");
        AddFlag(0x0080, "Midpoint voltage");
        AddFlag(0x0100, "Overload");
        AddFlag(0x0200, "DC ripple");
        AddFlag(0x1000, "Short circuit");
        AddFlag(0x2000, "BMS lockout");
        var unknownFlags = value & ~0x33FF;
        if (unknownFlags != 0)
            reasons.Add($"Unknown flags 0x{unknownFlags:X4}");
        return string.Join(", ", reasons);

        void AddFlag(ushort flag, string description)
        {
            if ((value & flag) != 0)
                reasons.Add(description);
        }
    }
}
