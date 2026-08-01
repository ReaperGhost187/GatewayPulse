using System.Text.Json;
using System.Text.Json.Nodes;
using GatewayPulse.ServiceHosting;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class DashboardPreferencesTests
{
    [Fact]
    public void CreateDefaults_ShowsFullRecommendedDashboard()
    {
        var preferences = DashboardPreferences.CreateDefaults();

        Assert.True(preferences.Cards.GatewayStatus);
        Assert.True(preferences.Cards.ConfiguredFrequency);
        Assert.True(preferences.Cards.Activity);
        Assert.True(preferences.Cards.PowerSystem);
        Assert.True(preferences.Cards.PowerEvents);
        Assert.True(preferences.Cards.WinlinkActivityToday);
        Assert.True(preferences.Cards.ScanChannels);
        Assert.True(preferences.Cards.StationConnectionCounts);
        Assert.True(preferences.Cards.UptimeSinceLastStart);
        Assert.True(preferences.Cards.Last50Connections);
        Assert.True(preferences.Cards.RecentWinlinkActivity);
        Assert.True(preferences.Cards.AdvancedDiagnostics);

        Assert.True(preferences.PowerTelemetry.StateOfCharge);
        Assert.True(preferences.PowerTelemetry.Voltage);
        Assert.True(preferences.PowerTelemetry.Current);
        Assert.True(preferences.PowerTelemetry.Power);
        Assert.True(preferences.PowerTelemetry.ConsumedAmpHours);
        Assert.True(preferences.PowerTelemetry.EstimatedRuntime);
        Assert.True(preferences.PowerTelemetry.ChargingDischargingState);
        Assert.True(preferences.PowerTelemetry.BatteryProtectOutput);
        Assert.True(preferences.PowerTelemetry.Alarm);
        Assert.True(preferences.PowerTelemetry.Rssi);
        Assert.True(preferences.PowerTelemetry.DeviceNameModel);
    }

    [Fact]
    public void Normalize_FillsMissingNestedObjects()
    {
        var preferences = DashboardPreferences.Normalize(null);

        Assert.NotNull(preferences.Cards);
        Assert.NotNull(preferences.PowerTelemetry);
        Assert.True(preferences.Cards.PowerSystem);
        Assert.True(preferences.PowerTelemetry.Voltage);
    }

    [Fact]
    public void Preferences_PersistUnderDashboardWithoutClearingDemoMode()
    {
        var root = JsonNode.Parse("""
            {
              "Dashboard": {
                "DemoMode": false,
                "RefreshSeconds": 5,
                "Theme": "OLED"
              }
            }
            """)!.AsObject();

        var preferences = DashboardPreferences.CreateDefaults();
        preferences.Cards.Activity = false;
        preferences.PowerTelemetry.Voltage = false;

        var dashboard = root["Dashboard"]!.AsObject();
        dashboard["Preferences"] = JsonSerializer.SerializeToNode(preferences, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var reloaded = JsonNode.Parse(json)!.AsObject();
        Assert.False(reloaded["Dashboard"]!["DemoMode"]!.GetValue<bool>());

        var loaded = reloaded["Dashboard"]!["Preferences"].Deserialize<DashboardPreferences>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        loaded = DashboardPreferences.Normalize(loaded);

        Assert.False(loaded.Cards.Activity);
        Assert.True(loaded.Cards.PowerSystem);
        Assert.False(loaded.PowerTelemetry.Voltage);
        Assert.True(loaded.PowerTelemetry.Current);
    }

    [Fact]
    public void ResetToDefaults_RestoresHiddenCardsAndTelemetry()
    {
        var customized = new DashboardPreferences
        {
            Cards = new DashboardCardPreferences
            {
                PowerSystem = false,
                PowerEvents = false,
                AdvancedDiagnostics = false
            },
            PowerTelemetry = new PowerTelemetryPreferences
            {
                StateOfCharge = false,
                Alarm = false,
                Rssi = false
            }
        };

        var reset = DashboardPreferences.CreateDefaults();
        Assert.False(customized.Cards.PowerSystem);
        Assert.True(reset.Cards.PowerSystem);
        Assert.True(reset.Cards.PowerEvents);
        Assert.True(reset.PowerTelemetry.StateOfCharge);
        Assert.True(reset.PowerTelemetry.Alarm);
        Assert.True(reset.PowerTelemetry.Rssi);
    }
}
