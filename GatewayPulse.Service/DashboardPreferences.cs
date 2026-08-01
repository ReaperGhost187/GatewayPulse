namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Display-only dashboard preferences. Does not affect monitoring, APIs, alarms, or history.
/// </summary>
public sealed class DashboardPreferences
{
    public DashboardCardPreferences Cards { get; set; } = new();
    public PowerTelemetryPreferences PowerTelemetry { get; set; } = new();
    public RfTelemetryPreferences RfTelemetry { get; set; } = new();

    public static DashboardPreferences CreateDefaults() => Normalize(new DashboardPreferences());

    public static DashboardPreferences Normalize(DashboardPreferences? preferences)
    {
        preferences ??= new DashboardPreferences();
        preferences.Cards ??= new DashboardCardPreferences();
        preferences.PowerTelemetry ??= new PowerTelemetryPreferences();
        preferences.RfTelemetry ??= new RfTelemetryPreferences();
        return preferences;
    }
}

public sealed class DashboardCardPreferences
{
    public bool GatewayStatus { get; set; } = true;
    public bool ConfiguredFrequency { get; set; } = true;
    public bool Activity { get; set; } = true;
    public bool PowerSystem { get; set; } = true;
    public bool PowerEvents { get; set; } = true;
    public bool RfPower { get; set; } = true;
    public bool WinlinkActivityToday { get; set; } = true;
    public bool ScanChannels { get; set; } = true;
    public bool StationConnectionCounts { get; set; } = true;
    public bool UptimeSinceLastStart { get; set; } = true;
    public bool Last50Connections { get; set; } = true;
    public bool RecentWinlinkActivity { get; set; } = true;
    public bool AdvancedDiagnostics { get; set; } = true;
}

/// <summary>
/// Secondary RF metrics visibility. Primary Forward/Reflected/SWR/Peak are always shown.
/// </summary>
public sealed class RfTelemetryPreferences
{
    public bool ReturnLoss { get; set; } = true;
    public bool Dbm { get; set; } = true;
    public bool Resistance { get; set; } = true;
    public bool Reactance { get; set; } = true;
    public bool Impedance { get; set; } = true;
    public bool Phase { get; set; } = true;
    public bool PowerRange { get; set; } = true;
    public bool MeterMode { get; set; } = true;
    public bool TxState { get; set; } = true;
    public bool ConnectionState { get; set; } = true;
    public bool LastUpdate { get; set; } = true;
    public bool ProtocolStatus { get; set; } = true;
}

public sealed class PowerTelemetryPreferences
{
    public bool StateOfCharge { get; set; } = true;
    public bool Voltage { get; set; } = true;
    public bool Current { get; set; } = true;
    public bool Power { get; set; } = true;
    public bool ConsumedAmpHours { get; set; } = true;
    public bool EstimatedRuntime { get; set; } = true;
    public bool ChargingDischargingState { get; set; } = true;
    public bool BatteryProtectOutput { get; set; } = true;
    public bool Alarm { get; set; } = true;
    public bool Rssi { get; set; } = true;
    public bool DeviceNameModel { get; set; } = true;
}
