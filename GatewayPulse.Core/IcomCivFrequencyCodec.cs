namespace GatewayPulse.Core;

/// <summary>
/// Pure helpers for Icom CI-V read-frequency (command 0x03) frames.
/// </summary>
public static class IcomCivFrequencyCodec
{
    public const byte Preamble = 0xFE;
    public const byte Eom = 0xFD;
    public const byte ControllerAddress = 0xE0;
    public const byte ReadFreqCommand = 0x03;

    public static byte[] BuildReadFrequencyRequest(byte radioAddress) =>
    [
        Preamble, Preamble, radioAddress, ControllerAddress, ReadFreqCommand, Eom
    ];

    public static bool TryParseAddress(string? hex, out byte address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(hex))
            return false;
        var s = hex.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return byte.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out address) && address != 0;
    }

    /// <summary>
    /// Decode a CI-V response buffer that contains command 0x03 + 5 BCD frequency bytes.
    /// </summary>
    public static bool TryDecodeFrequencyHz(ReadOnlySpan<byte> buffer, byte radioAddress, out long frequencyHz)
    {
        frequencyHz = 0;
        for (var i = 0; i + 10 < buffer.Length; i++)
        {
            if (buffer[i] != Preamble || buffer[i + 1] != Preamble)
                continue;
            // FE FE E0 <radio> 03 d0 d1 d2 d3 d4 FD
            if (buffer[i + 2] != ControllerAddress)
                continue;
            if (buffer[i + 3] != radioAddress)
                continue;
            if (buffer[i + 4] != ReadFreqCommand)
                continue;
            if (buffer[i + 10] != Eom)
                continue;

            frequencyHz = DecodeBcdFrequencyHz(buffer.Slice(i + 5, 5));
            return frequencyHz > 0;
        }

        return false;
    }

    public static long DecodeBcdFrequencyHz(ReadOnlySpan<byte> fiveBytes)
    {
        if (fiveBytes.Length < 5)
            return 0;

        long hz = 0;
        long place = 1;
        for (var i = 0; i < 5; i++)
        {
            var b = fiveBytes[i];
            var lo = b & 0x0F;
            var hi = (b >> 4) & 0x0F;
            if (lo > 9 || hi > 9)
                return 0;
            hz += lo * place;
            place *= 10;
            hz += hi * place;
            place *= 10;
        }

        return hz;
    }
}
