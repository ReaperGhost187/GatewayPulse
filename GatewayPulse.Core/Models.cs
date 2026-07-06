namespace GatewayPulse.Core;

public sealed class GatewayPulseOptions
{
    public string GatewayName { get; set; } = "NND0WA SHARES Gateway";
    public string Callsign { get; set; } = "NND0WA";
    public bool PrivacyMode { get; set; } = true;
    public string RelayLogs { get; set; } = @"C:\RMS\RMS Relay\Logs";
    public string TrimodeLogs { get; set; } = @"C:\RMS\RMS Trimode\Logs";
    public string TrimodeIni { get; set; } = @"C:\RMS\RMS Trimode\RMS Trimode.ini";
    public string TrimodeHost { get; set; } = "127.0.0.1";
    public int TrimodeCommandPort { get; set; } = 8510;
    public bool ShowConnectingStations { get; set; } = true;
}

public sealed class PushoverOptions
{
    public bool Enabled { get; set; }
    public string UserKey { get; set; } = "";
    public string ApiToken { get; set; } = "";
    public string Device { get; set; } = "";
    public int Priority { get; set; } = 0;
    public int CooldownMinutes { get; set; } = 5;
    public bool SendRecoveryAlerts { get; set; } = true;
}

public sealed class AlertOptions
{
    public bool RelayOffline { get; set; } = true;
    public bool TrimodeOffline { get; set; } = true;
    public bool ScannerStopped { get; set; } = true;
    public bool CommandPortFailed { get; set; } = true;
    public bool Recovery { get; set; } = true;
    public bool StationConnected { get; set; } = false;
}

public sealed record GatewayEvent(string Timestamp, string Source, string Type, string Detail);

public sealed class ScanChannel
{
    public int Number { get; set; }
    public string FrequencyKhz { get; set; } = "";
    public int FrequencyHz { get; set; }
    public string Mode { get; set; } = "PACTOR";
    public bool Active { get; set; }
    public string ServiceCode { get; set; } = "";
}

public sealed class GatewayStatus
{
    public string GatewayName { get; set; } = "";
    public string Callsign { get; set; } = "";
    public bool Healthy { get; set; }
    public bool? RelayRunning { get; set; }
    public bool TrimodeSeen { get; set; }

    public bool? ScannerEnabled { get; set; }
    public string ScannerStatus { get; set; } = "Unknown";
    public string CommandPortStatus { get; set; } = "Not checked";

    public string MemoryReadStatus { get; set; } = "Not checked";
    public string MemoryAddress { get; set; } = "";

    public string? LastRelayEvent { get; set; }
    public string? LastTrimodeEvent { get; set; }
    public string? LastConnection { get; set; }
    public string? LastDisconnect { get; set; }
    public string? LastStation { get; set; }
    public int? LastSfi { get; set; }
    public int SessionsToday { get; set; }

    public string CurrentFrequencyKhz { get; set; } = "--";
    public string DialFrequencyKhz { get; set; } = "--";
    public string CurrentMode { get; set; } = "PACTOR";
    public string LiveFrequencySource { get; set; } = "Configured";

    public List<ScanChannel> ScanChannels { get; set; } = new();
    public List<object> StationCounts { get; set; } = new();
    public List<GatewayEvent> RecentEvents { get; set; } = new();
    public string LastScan { get; set; } = "";
}
