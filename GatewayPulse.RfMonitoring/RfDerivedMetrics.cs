namespace GatewayPulse.RfMonitoring;

/// <summary>
/// Metrics derived from LP-100A polled display fields using standard RF formulas.
/// Reflected power and return loss are not native serial fields — label them calculated.
/// Serial 'P' values are display snapshots, not RF-envelope samples.
/// </summary>
public static class RfDerivedMetrics
{
    public const string PeakHoldHint =
        "Set LP-100A meter mode to Peak (Peak Hold) for PACTOR; Gateway Pulse does not send F/A/M.";

    public static decimal? ReflectedPowerWatts(decimal forwardWatts, decimal swr)
    {
        if (forwardWatts < 0m || swr < 1m)
            return null;
        if (swr <= 1m)
            return 0m;
        var gamma = (swr - 1m) / (swr + 1m);
        return forwardWatts * gamma * gamma;
    }

    public static bool IsSwrAtResolutionFloor(decimal? swr) =>
        swr is decimal s && s <= 1.00m;

    public static decimal? ReturnLossDb(decimal swr)
    {
        if (swr <= 1m)
            return 60m;
        var gamma = (double)((swr - 1m) / (swr + 1m));
        if (gamma <= 0)
            return 60m;
        var rl = -20.0 * Math.Log10(gamma);
        if (!double.IsFinite(rl))
            return 60m;
        return (decimal)Math.Min(rl, 60.0);
    }

    public static decimal? ResistanceOhms(decimal impedanceOhms, decimal phaseDegrees)
    {
        var radians = (double)phaseDegrees * Math.PI / 180.0;
        return impedanceOhms * (decimal)Math.Cos(radians);
    }

    public static decimal? ReactanceOhms(decimal impedanceOhms, decimal phaseDegrees)
    {
        var radians = (double)phaseDegrees * Math.PI / 180.0;
        return impedanceOhms * (decimal)Math.Sin(radians);
    }

    public static string PowerRangeText(int range) => range switch
    {
        0 => "High",
        1 => "Mid",
        2 => "Low",
        _ => "Unknown"
    };

    public static string MeterModeText(int mode) => mode switch
    {
        0 => "Average",
        1 => "Peak",
        2 => "Tune",
        _ => "Unknown"
    };

    public static string AlarmSetpointText(int index) => index switch
    {
        0 => "Off",
        1 => "1.5",
        2 => "2.0",
        3 => "2.5",
        4 => "3.0",
        5 => "User",
        _ => "Unknown"
    };
}
