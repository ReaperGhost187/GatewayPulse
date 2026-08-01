using System.Security.Cryptography;
using System.Text.Json;
using GatewayPulse.PowerMonitoring;

namespace GatewayPulse.VictronMonitor.Configuration;

public sealed class ConfiguredVictronDevice : IDisposable
{
    public required string Type { get; init; }
    public required string Address { get; init; }
    public byte[]? AdvertisementKey { get; private set; }
    public string? Error { get; init; }
    public bool IsUsable => AdvertisementKey is not null && Error is null;

    internal static ConfiguredVictronDevice Usable(string type, string address, byte[] key) => new()
    {
        Type = type,
        Address = address,
        AdvertisementKey = key
    };

    internal static ConfiguredVictronDevice Invalid(string type, string address, string error) => new()
    {
        Type = type,
        Address = address,
        Error = error
    };

    public void Dispose()
    {
        if (AdvertisementKey is not null)
        {
            CryptographicOperations.ZeroMemory(AdvertisementKey);
            AdvertisementKey = null;
        }
    }
}

public sealed class VictronMultiDeviceConfiguration : IDisposable
{
    public IReadOnlyList<ConfiguredVictronDevice> Devices { get; }
    public PowerThresholds Thresholds { get; }
    public TimeSpan StaleAfter { get; }

    private VictronMultiDeviceConfiguration(
        IReadOnlyList<ConfiguredVictronDevice> devices,
        PowerThresholds thresholds,
        TimeSpan staleAfter)
    {
        Devices = devices;
        Thresholds = thresholds;
        StaleAfter = staleAfter;
    }

    public static VictronMultiDeviceConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(path)));
        if (!TryGetProperty(document.RootElement, "VictronMonitor", out var monitorSection) ||
            monitorSection.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The configuration does not contain a VictronMonitor section.");
        }

        var thresholds = ReadThresholds(monitorSection);
        var staleAfterSeconds = ReadInt32(monitorSection, "Thresholds", "StaleAfterSeconds", 30);
        if (staleAfterSeconds <= 0)
            throw new InvalidDataException("VictronMonitor:Thresholds:StaleAfterSeconds must be positive.");

        var devices = new List<ConfiguredVictronDevice>();
        if (!TryGetProperty(monitorSection, "Devices", out var deviceArray) || deviceArray.ValueKind != JsonValueKind.Array)
            return new VictronMultiDeviceConfiguration(devices, thresholds, TimeSpan.FromSeconds(staleAfterSeconds));

        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in deviceArray.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object || !ReadBoolean(element, "Enabled", false))
                continue;

            var type = NormalizeType(ReadString(element, "Type"));
            var address = NormalizeAddress(ReadString(element, "Address"));
            var keyFile = ReadString(element, "KeyFile");
            var error = Validate(type, address, keyFile, seenAddresses);
            if (error is not null)
            {
                devices.Add(ConfiguredVictronDevice.Invalid(type, address, error));
                continue;
            }

            byte[]? key = null;
            try
            {
                key = ReadAdvertisementKey(keyFile!);
                devices.Add(ConfiguredVictronDevice.Usable(type, address, key));
                key = null;
            }
            catch (InvalidDataException)
            {
                devices.Add(ConfiguredVictronDevice.Invalid(
                    type,
                    address,
                    $"{type} advertisement key file must contain exactly 32 hexadecimal characters."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                devices.Add(ConfiguredVictronDevice.Invalid(
                    type,
                    address,
                    File.Exists(keyFile) 
                        ? $"{type} advertisement key file could not be read."
                        : $"{type} advertisement key file is missing."));
            }
            finally
            {
                if (key is not null)
                    CryptographicOperations.ZeroMemory(key);
            }
        }

        return new VictronMultiDeviceConfiguration(devices, thresholds, TimeSpan.FromSeconds(staleAfterSeconds));
    }

    private static byte[] ReadAdvertisementKey(string path)
    {
        var contents = File.ReadAllBytes(path);
        try
        {
            var start = contents.Length >= 3 && contents[0] == 0xEF && contents[1] == 0xBB && contents[2] == 0xBF
                ? 3
                : 0;
            var end = contents.Length;
            while (start < end && IsAsciiWhitespace(contents[start]))
                start++;
            while (end > start && IsAsciiWhitespace(contents[end - 1]))
                end--;
            if (end - start != 32)
                throw new InvalidDataException();

            var key = new byte[16];
            try
            {
                for (var index = 0; index < key.Length; index++)
                {
                    var high = HexValue(contents[start + index * 2]);
                    var low = HexValue(contents[start + index * 2 + 1]);
                    if (high < 0 || low < 0)
                        throw new InvalidDataException();
                    key[index] = checked((byte)((high << 4) | low));
                }
                return key;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contents);
        }
    }

    private static bool IsAsciiWhitespace(byte value) => value is 0x09 or 0x0A or 0x0D or 0x20;

    private static int HexValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        _ => -1
    };

    public void Dispose()
    {
        foreach (var device in Devices)
            device.Dispose();
    }

    private static string? Validate(
        string type,
        string address,
        string? keyFile,
        ISet<string> seenAddresses)
    {
        if (type is not (PowerDeviceTypes.BatteryProtect or PowerDeviceTypes.SmartShunt))
            return "Victron device type must be BatteryProtect or SmartShunt.";
        if (address.Length != 12 || !address.All(char.IsAsciiHexDigit))
            return $"{type} Bluetooth address is missing or invalid.";
        if (!seenAddresses.Add(address))
            return $"{type} Bluetooth address duplicates another enabled device.";
        if (string.IsNullOrWhiteSpace(keyFile))
            return $"{type} advertisement key file is not configured.";
        if (!Path.IsPathRooted(keyFile))
            return $"{type} advertisement key file path must be absolute.";
        if (!File.Exists(keyFile))
            return $"{type} advertisement key file is missing.";
        return null;
    }

    private static PowerThresholds ReadThresholds(JsonElement monitorSection)
    {
        if (!TryGetProperty(monitorSection, "Thresholds", out var value) || value.ValueKind != JsonValueKind.Object)
            return new PowerThresholds();
        return new PowerThresholds
        {
            StateOfChargeWarningPercent = ReadDecimal(value, "StateOfChargeWarningPercent", 30m),
            StateOfChargeCriticalPercent = ReadDecimal(value, "StateOfChargeCriticalPercent", 15m),
            WeakSignalRssi = ReadInt32(value, "WeakSignalRssi", -85),
            IdleCurrentAmps = ReadDecimal(value, "IdleCurrentAmps", 0.2m),
            LowVoltageWarning = ReadDecimal(value, "LowVoltageWarning", 11.8m),
            LowVoltageCritical = ReadDecimal(value, "LowVoltageCritical", 11.0m),
            HighVoltageWarning = ReadDecimal(value, "HighVoltageWarning", 15.0m)
        };
    }

    private static int ReadInt32(JsonElement parent, string section, string property, int defaultValue) =>
        TryGetProperty(parent, section, out var child) && child.ValueKind == JsonValueKind.Object
            ? ReadInt32(child, property, defaultValue)
            : defaultValue;

    private static int ReadInt32(JsonElement parent, string property, int defaultValue) =>
        TryGetProperty(parent, property, out var value) && value.TryGetInt32(out var result) ? result : defaultValue;

    private static decimal ReadDecimal(JsonElement parent, string property, decimal defaultValue) =>
        TryGetProperty(parent, property, out var value) && value.TryGetDecimal(out var result) ? result : defaultValue;

    private static bool ReadBoolean(JsonElement parent, string property, bool defaultValue) =>
        TryGetProperty(parent, property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;

    private static string? ReadString(JsonElement parent, string property) =>
        TryGetProperty(parent, property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string NormalizeType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "batteryprotect" => PowerDeviceTypes.BatteryProtect,
        "smartshunt" => PowerDeviceTypes.SmartShunt,
        _ => type?.Trim() ?? "Unknown"
    };

    internal static string NormalizeAddress(string? address) =>
        string.IsNullOrWhiteSpace(address)
            ? ""
            : new string(address.Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();

    private static bool TryGetProperty(JsonElement parent, string name, out JsonElement value)
    {
        foreach (var property in parent.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
