namespace GatewayPulse.RfMonitoring;

/// <summary>
/// Normalized RF wattmeter telemetry. Values are LP-100A display snapshots from the
/// polled 'P' frame (not high-rate RF-envelope samples). Nullables omit unsupported fields.
/// </summary>
public sealed class RfTelemetry
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUpdate { get; set; } = DateTimeOffset.UtcNow;
    public bool Connected { get; set; }
    public string Provider { get; set; } = "unknown";
    public string Device { get; set; } = "Unknown RF meter";
    public string ConnectionState { get; set; } = RfConnectionStates.Disconnected;
    public string? ProtocolStatus { get; set; }
    public string? Error { get; set; }
    public bool Stale { get; set; }
    public bool Transmitting { get; set; }

    public decimal? ForwardPowerWatts { get; set; }
    public decimal? PeakForwardPowerWatts { get; set; }
    public decimal? LastPeakForwardPowerWatts { get; set; }

    /// <summary>Derived reflected power (same as <see cref="ReflectedPowerWattsCalculated"/>).</summary>
    public decimal? ReflectedPowerWatts { get; set; }
    /// <summary>Explicit calculated reflected power for API/UI honesty.</summary>
    public decimal? ReflectedPowerWattsCalculated { get; set; }
    public string ReflectedPowerSource { get; set; } = RfReflectedPowerSources.Calculated;

    public decimal? Swr { get; set; }
    /// <summary>True when reported SWR is exactly 1.00 (meter resolution floor).</summary>
    public bool SwrAtResolutionFloor { get; set; }
    public decimal? ReturnLossDb { get; set; }
    public decimal? Dbm { get; set; }
    public decimal? ImpedanceOhms { get; set; }
    public decimal? PhaseDegrees { get; set; }
    public decimal? ResistanceOhms { get; set; }
    public decimal? ReactanceOhms { get; set; }
    public string? PowerRange { get; set; }
    /// <summary>Meter mode from serial: Average / Peak / Tune. Prefer Peak Hold for PACTOR.</summary>
    public string? MeterMode { get; set; }
    public string? MeterModeHint { get; set; }
    public string? MeterAlarmSetpoint { get; set; }
    public string? Callsign { get; set; }
    public string? ComPort { get; set; }
    public int? BaudRate { get; set; }

    /// <summary>Most recent raw LP-100A frame body (no leading ';') for front-panel compare.</summary>
    public string? LastRawFrameBody { get; set; }
    /// <summary>Recent raw frame bodies (newest last), for Settings/debug.</summary>
    public List<string> RecentRawFrameBodies { get; set; } = [];

    public List<RfEvent> Events { get; set; } = [];
}

public sealed class RfEvent
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Type { get; set; } = "";
    public string Detail { get; set; } = "";
}

public static class RfConnectionStates
{
    public const string Connected = "Connected";
    public const string Disconnected = "Disconnected";
    public const string Stale = "Stale";
    public const string Error = "Error";
    public const string Connecting = "Connecting";
}

public static class RfStatuses
{
    public const string Healthy = "Healthy";
    public const string Warning = "Warning";
    public const string Critical = "Critical";
    public const string Unavailable = "Unavailable";
    public const string Idle = "Idle";
    public const string Transmitting = "Transmitting";
}
