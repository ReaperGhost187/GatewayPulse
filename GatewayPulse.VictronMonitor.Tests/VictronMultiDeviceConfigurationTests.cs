using GatewayPulse.PowerMonitoring;
using GatewayPulse.VictronMonitor.Configuration;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class VictronMultiDeviceConfigurationTests
{
    [Fact]
    public void Load_OneInvalidDevice_PreservesUsableBatteryProtectWithoutExposingKeyDetails()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gateway-pulse-multi-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var batteryKey = Path.Combine(directory, "victron.key");
        var missingShuntKey = Path.Combine(directory, "smartshunt.key");
        var configurationPath = Path.Combine(directory, "appsettings.json");
        File.WriteAllText(batteryKey, string.Concat(Enumerable.Repeat("01", 16)));
        File.WriteAllText(configurationPath, $$"""
            {
              "VictronMonitor": {
                "Thresholds": {
                  "StaleAfterSeconds": 45,
                  "IdleCurrentAmps": 0.15
                },
                "Devices": [
                  {
                    "type": "BatteryProtect",
                    "address": "D5:11:30:C1:55:16",
                    "keyFile": "{{batteryKey.Replace("\\", "\\\\")}}",
                    "enabled": true
                  },
                  {
                    "type": "SmartShunt",
                    "address": "AA:BB:CC:DD:EE:FF",
                    "keyFile": "{{missingShuntKey.Replace("\\", "\\\\")}}",
                    "enabled": true
                  }
                ]
              }
            }
            """);

        try
        {
            using var result = VictronMultiDeviceConfiguration.Load(configurationPath);

            Assert.Equal(2, result.Devices.Count);
            var batteryProtect = Assert.Single(result.Devices, device => device.Type == PowerDeviceTypes.BatteryProtect);
            Assert.True(batteryProtect.IsUsable);
            Assert.Equal(16, batteryProtect.AdvertisementKey!.Length);
            var smartShunt = Assert.Single(result.Devices, device => device.Type == PowerDeviceTypes.SmartShunt);
            Assert.False(smartShunt.IsUsable);
            Assert.Null(smartShunt.AdvertisementKey);
            Assert.Contains("key file is missing", smartShunt.Error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(missingShuntKey, smartShunt.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(TimeSpan.FromSeconds(45), result.StaleAfter);
            Assert.Equal(0.15m, result.Thresholds.IdleCurrentAmps);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_KeyWithUtf8BomAndWhitespace_ParsesWithoutRetainingKeyText()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gateway-pulse-key-format-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var keyPath = Path.Combine(directory, "victron.key");
        var configPath = Path.Combine(directory, "appsettings.json");
        var keyCharacters = System.Text.Encoding.ASCII.GetBytes(new string('A', 32));
        File.WriteAllBytes(keyPath, [0xEF, 0xBB, 0xBF, 0x0A, .. keyCharacters, 0x0D, 0x0A]);
        File.WriteAllText(configPath, $$"""
            {
              "VictronMonitor": {
                "Devices": [
                  {
                    "type": "BatteryProtect",
                    "address": "D5:11:30:C1:55:16",
                    "keyFile": "{{keyPath.Replace("\\", "\\\\")}}",
                    "enabled": true
                  }
                ]
              }
            }
            """);

        try
        {
            using var result = VictronMultiDeviceConfiguration.Load(configPath);
            var device = Assert.Single(result.Devices);
            Assert.True(device.IsUsable);
            Assert.All(device.AdvertisementKey!, value => Assert.Equal(0xAA, value));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_DisabledUnconfiguredSmartShunt_IsNotPublishedAsADevice()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            {
              "VictronMonitor": {
                "Devices": [
                  { "type": "SmartShunt", "address": "", "keyFile": "C:\\PWM\\smartshunt.key", "enabled": false }
                ]
              }
            }
            """);

        try
        {
            using var result = VictronMultiDeviceConfiguration.Load(path);
            Assert.Empty(result.Devices);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
