namespace GatewayPulse.ServiceHosting;

public sealed class DashboardOptions
{
    /// <summary>
    /// When true, the service may launch the Victron collector in --mock mode and accept
    /// mock telemetry. Must remain false in production builds.
    /// </summary>
    public bool DemoMode { get; set; }

    public int RefreshSeconds { get; set; } = 5;
    public string Theme { get; set; } = "OLED";

    /// <summary>
    /// Display-only card/field visibility. Defaults show the full recommended dashboard.
    /// </summary>
    public DashboardPreferences Preferences { get; set; } = DashboardPreferences.CreateDefaults();
}
