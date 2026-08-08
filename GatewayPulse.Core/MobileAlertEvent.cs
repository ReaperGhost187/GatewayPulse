namespace GatewayPulse.Core;

/// <summary>
/// Canonical normalized alert event for mobile (APNs) fan-out.
/// Emitted after existing alert decisions; does not replace Pushover.
/// </summary>
public sealed class MobileAlertEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "";
    public string Source { get; set; } = "";
    public string Severity { get; set; } = MobileAlertSeverity.Warning;
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsRecovery { get; set; }
    public decimal? MeasuredValue { get; set; }
    public decimal? Threshold { get; set; }
    public string? Unit { get; set; }
    public decimal? FrequencyKhz { get; set; }
    public string? GatewayName { get; set; }

    /// <summary>
    /// Optional local dedupe key for delivery-outcome tracking (e.g. RF condition).
    /// Not included in APNs payloads.
    /// </summary>
    public string? DedupeKey { get; set; }
}

public static class MobileAlertSeverity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";
}

/// <summary>Stable alert type identifiers for GatewayPulse Mobile.</summary>
public static class MobileAlertTypes
{
    public const string GatewayRelayOffline = "gateway.relay-offline";
    public const string GatewayTrimodeOffline = "gateway.trimode-offline";
    public const string GatewayScannerStopped = "gateway.scanner-stopped";
    public const string GatewayRecovery = "gateway.recovery";
    public const string GatewayStationConnected = "gateway.station-connected";

    public const string RfDisconnected = "rf.disconnected";
    public const string RfStale = "rf.stale";
    public const string RfRecovery = "rf.recovery";
    public const string RfSwrWarning = "rf.swr-warning";
    public const string RfSwrCritical = "rf.swr-critical";
    public const string RfReflected = "rf.reflected";
    public const string RfHighPower = "rf.high-power";
    public const string RfCleared = "rf.cleared";

    public const string PowerWarning = "power.warning";
    public const string PowerCritical = "power.critical";
    public const string PowerAlarm = "power.alarm";
    public const string PowerDeviceDisconnected = "power.device-disconnected";
    public const string PowerDeviceStale = "power.device-stale";
    public const string PowerOutputOff = "power.output-off";
    public const string PowerRecovery = "power.recovery";
}

/// <summary>Result of accepting an alert into the mobile push pipeline (not APNs delivery).</summary>
public sealed class MobilePublishAck
{
    public bool Accepted { get; init; }

    /// <summary>queued | disabled | dropped | noop</summary>
    public string Outcome { get; init; } = "";
}

public static class MobilePublishOutcomes
{
    public const string Queued = "queued";
    public const string Disabled = "disabled";
    public const string Dropped = "dropped";
    public const string Noop = "noop";
}

public interface IMobileAlertPublisher
{
    /// <summary>
    /// Enqueues a mobile alert for bounded background APNs delivery.
    /// Completes when the queue accepts/rejects the event — never waits on Apple network I/O.
    /// </summary>
    Task<MobilePublishAck> PublishAsync(MobileAlertEvent alertEvent, CancellationToken cancellationToken = default);
}

/// <summary>No-op publisher used when mobile push is not registered.</summary>
public sealed class NullMobileAlertPublisher : IMobileAlertPublisher
{
    public static NullMobileAlertPublisher Instance { get; } = new();

    public Task<MobilePublishAck> PublishAsync(MobileAlertEvent alertEvent, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MobilePublishAck { Accepted = true, Outcome = MobilePublishOutcomes.Noop });
}
