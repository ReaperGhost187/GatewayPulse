using System.Text.Json.Nodes;
using GatewayPulse.Core;
using GatewayPulse.ServiceHosting;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class SettingsSectionMergeTests
{
    [Fact]
    public void ApplyRadioCat_NullIncoming_PreservesExisting()
    {
        var gatewayPulse = JsonNode.Parse("""
            {
              "GatewayName": "Test",
              "RadioCat": {
                "Enabled": true,
                "PortName": "COM5",
                "BaudRate": 19200,
                "CivAddress": "94"
              }
            }
            """)!.AsObject();

        SettingsSectionMerge.ApplyRadioCat(gatewayPulse, null);

        Assert.Equal("COM5", gatewayPulse["RadioCat"]?["PortName"]?.GetValue<string>());
        Assert.True(gatewayPulse["RadioCat"]?["Enabled"]?.GetValue<bool>());
    }

    [Fact]
    public void ApplyRadioCat_NormalizesBarePortNumber()
    {
        var gatewayPulse = new JsonObject();
        SettingsSectionMerge.ApplyRadioCat(gatewayPulse, new RadioCatOptions
        {
            Enabled = true,
            PortName = "5",
            BaudRate = 19200,
            CivAddress = "94"
        });

        Assert.Equal("COM5", gatewayPulse["RadioCat"]?["PortName"]?.GetValue<string>());
        Assert.True(gatewayPulse["RadioCat"]?["Enabled"]?.GetValue<bool>());
    }

    [Fact]
    public void ApplyLp100Monitor_NullIncoming_PreservesExisting()
    {
        var root = JsonNode.Parse("""
            {
              "Lp100Monitor": {
                "Enabled": true,
                "Port": "COM4",
                "BaudRate": 115200
              },
              "GatewayPulse": {
                "RadioCat": { "Enabled": true, "PortName": "COM5" }
              }
            }
            """)!.AsObject();

        SettingsSectionMerge.ApplyLp100Monitor(root, null);

        Assert.Equal("COM4", root["Lp100Monitor"]?["Port"]?.GetValue<string>());
        Assert.Equal("COM5", root["GatewayPulse"]?["RadioCat"]?["PortName"]?.GetValue<string>());
    }

    [Fact]
    public void ApplyLp100Monitor_NormalizesBarePortAndDoesNotTouchRadioCat()
    {
        var root = JsonNode.Parse("""
            {
              "GatewayPulse": {
                "RadioCat": { "Enabled": true, "PortName": "COM5" }
              }
            }
            """)!.AsObject();

        SettingsSectionMerge.ApplyLp100Monitor(root, new Lp100MonitorOptions
        {
            Enabled = true,
            Port = "4",
            BaudRate = 115200,
            OutputPath = @"C:\PWM\RfTelemetry.json",
            LogsPath = @"C:\PWM\logs"
        });

        Assert.Equal("COM4", root["Lp100Monitor"]?["Port"]?.GetValue<string>());
        Assert.Equal("COM5", root["GatewayPulse"]?["RadioCat"]?["PortName"]?.GetValue<string>());
    }
}
