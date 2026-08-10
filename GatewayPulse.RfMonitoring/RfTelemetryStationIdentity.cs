namespace GatewayPulse.RfMonitoring;

/// <summary>
/// Applies GatewayPulse station identity onto LP-100A telemetry for presentation.
/// Never falls back to the meter-programmed operator callsign.
/// </summary>
public static class RfTelemetryStationIdentity
{
    /// <summary>
    /// Replaces <see cref="RfTelemetry.Callsign"/> with the configured gateway/station
    /// callsign, or clears it when none is configured.
    /// </summary>
    public static RfTelemetry ApplyGatewayCallsign(RfTelemetry telemetry, string? gatewayCallsign)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        var configured = gatewayCallsign?.Trim();
        telemetry.Callsign = string.IsNullOrWhiteSpace(configured)
            ? null
            : configured.ToUpperInvariant();
        return telemetry;
    }
}
