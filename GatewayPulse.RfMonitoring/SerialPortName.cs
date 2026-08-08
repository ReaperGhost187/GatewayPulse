namespace GatewayPulse.RfMonitoring;

/// <summary>
/// Normalizes Windows serial port names to the COMn form expected by System.IO.Ports.
/// Accepts bare digits ("4"), mixed case ("com4"), and device-path prefixes ("\\.\COM4").
/// </summary>
public static class SerialPortName
{
    public static string Normalize(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
            return "";

        var trimmed = portName.Trim().ToUpperInvariant();

        // Device path form: \\.\COM4 or \\.\COM10
        const string devicePrefix = @"\\.\";
        if (trimmed.StartsWith(devicePrefix, StringComparison.Ordinal))
            trimmed = trimmed[devicePrefix.Length..];

        if (trimmed.StartsWith("COM", StringComparison.Ordinal)
            && trimmed.Length > 3
            && trimmed[3..].All(char.IsDigit))
        {
            return trimmed;
        }

        if (trimmed.All(char.IsDigit) && int.TryParse(trimmed, out var number) && number > 0)
            return $"COM{number}";

        return trimmed;
    }
}
