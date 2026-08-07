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
    public RadioCatOptions RadioCat { get; set; } = new();

    /// <summary>
    /// Optional live Trimode probing. Both stay OFF by default — TCP :8510 and
    /// process-memory reads have been observed to hitch/recycle RMS Trimode.
    /// GP still monitors Trimode via process presence + log/INI reads only.
    /// </summary>
    public TrimodeProbeOptions TrimodeProbe { get; set; } = new();
}

public sealed class TrimodeProbeOptions
{
    /// <summary>Send read-only SCAN on the Trimode command port. Default false.</summary>
    public bool CommandPortEnabled { get; set; }

    /// <summary>Read Trimode process memory for live frequency. Default false.</summary>
    public bool MemoryReadEnabled { get; set; }
}

/// <summary>
/// Optional radio frequency source for dashboard + TX history.
/// Prefer CI-V COM (CT-17 / USB CI-V) on unattended gateways; rigctld remains available.
/// </summary>
public sealed class RadioCatOptions
{
    public bool Enabled { get; set; }

    /// <summary>CivCom (default) or Rigctld.</summary>
    public string Mode { get; set; } = "CivCom";

    // --- CI-V serial (CT-17 / second CI-V interface; not Trimode's radio COM) ---
    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 19200;
    /// <summary>Icom CI-V address as hex string without 0x (IC-7300 default 94).</summary>
    public string CivAddress { get; set; } = "94";
    /// <summary>How often to poll frequency when enabled. Clamped 1–30.</summary>
    public int PollSeconds { get; set; } = 2;

    // --- Hamlib rigctld (legacy / optional) ---
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4532;
    public int TimeoutMs { get; set; } = 400;
}

public sealed class PushoverOptions
{
    public bool Enabled { get; set; }
    public string UserKey { get; set; } = "";
    public string ApiToken { get; set; } = "";
    public string Device { get; set; } = "";
    public int Priority { get; set; } = 0;
    public int CooldownMinutes { get; set; } = 5;
}

public sealed class AlertOptions
{
    public bool RelayOffline { get; set; } = true;
    public bool TrimodeOffline { get; set; } = true;
    public bool ScannerStopped { get; set; } = true;
    public bool Recovery { get; set; } = true;
    public bool StationConnected { get; set; } = false;
}

public sealed record GatewayEvent(string Timestamp, string Source, string Type, string Detail);

public sealed class StationConnection
{
    public string Timestamp { get; set; } = "";
    public string Station { get; set; } = "";
    public string Source { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class UptimeMetric
{
    public string Name { get; set; } = "";
    public bool Running { get; set; }
    public string LastStarted { get; set; } = "";
    public double Hours { get; set; }
    public string Display { get; set; } = "Unknown";
}

public sealed class HourlyActivity
{
    public string Hour { get; set; } = "";
    public int RelayConnections { get; set; }
    public int TrimodeConnections { get; set; }
    public int Disconnects { get; set; }
}

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
    public bool DemoMode { get; set; }
    public bool Healthy { get; set; }
    public bool? RelayRunning { get; set; }
    public bool TrimodeSeen { get; set; }

    public bool? ScannerEnabled { get; set; }
    public string ScannerStatus { get; set; } = "Unknown";
    public string CommandPortStatus { get; set; } = "Not checked";

    public string MemoryReadStatus { get; set; } = "Not checked";
    public string MemoryAddress { get; set; } = "";

    public string? LastRelayEvent { get; set; }
    public string? LastRelayStart { get; set; }
    public string? LastTrimodeEvent { get; set; }
    public string? LastTrimodeStart { get; set; }
    public string? LastConnection { get; set; }
    public string? LastDisconnect { get; set; }
    public string? LastStation { get; set; }
    public int? LastSfi { get; set; }
    public int SessionsToday { get; set; }

    public string CurrentFrequencyKhz { get; set; } = "--";
    public string DialFrequencyKhz { get; set; } = "--";
    public string CurrentMode { get; set; } = "PACTOR";
    public string LiveFrequencySource { get; set; } = "Configured";
    /// <summary>UTC time the reported frequency was last observed from Trimode memory/CAT.</summary>
    public DateTimeOffset? FrequencyUpdatedAt { get; set; }

    public List<ScanChannel> ScanChannels { get; set; } = new();
    public List<object> StationCounts { get; set; } = new();
    public List<StationConnection> RecentStationConnections { get; set; } = new();
    public List<HourlyActivity> HourlyActivity { get; set; } = new();
    public List<UptimeMetric> UptimeMetrics { get; set; } = new();
    public List<GatewayEvent> RecentEvents { get; set; } = new();
    public string LastScan { get; set; } = "";

    /// <summary>Dashboard full-status poll interval (seconds), from Dashboard:RefreshSeconds.</summary>
    public int RefreshSeconds { get; set; } = 5;

    /// <summary>Dashboard live scanner/frequency poll interval (seconds).</summary>
    public int LiveRadioSeconds { get; set; } = 1;
}
