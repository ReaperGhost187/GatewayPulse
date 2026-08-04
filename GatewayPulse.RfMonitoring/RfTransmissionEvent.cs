namespace GatewayPulse.RfMonitoring;

public static class FrequencySources
{
    public const string Winlink = "Winlink";
    public const string ManualCat = "Manual/CAT";
    public const string Unknown = "Unknown";
}

public static class FrequencyConfidenceLevels
{
    public const string Live = "Live";
    public const string Recent = "Recent";
    public const string Stale = "Stale";
    public const string Unknown = "Unknown";
}

public sealed class FrequencySnapshot
{
    public decimal? FrequencyKhz { get; init; }
    public string Source { get; init; } = FrequencySources.Unknown;
    public DateTimeOffset? UpdatedAt { get; init; }
    public double? AgeSecondsAtCapture { get; init; }
    public string Confidence { get; init; } = FrequencyConfidenceLevels.Unknown;

    public static FrequencySnapshot Unknown() => new();

    public static string ClassifyConfidence(double? ageSeconds)
    {
        if (ageSeconds is null)
            return FrequencyConfidenceLevels.Unknown;
        if (ageSeconds.Value <= 2)
            return FrequencyConfidenceLevels.Live;
        if (ageSeconds.Value <= 15)
            return FrequencyConfidenceLevels.Recent;
        return FrequencyConfidenceLevels.Stale;
    }

    public static FrequencySnapshot FromObservation(
        decimal? frequencyKhz,
        string source,
        DateTimeOffset? updatedAt,
        DateTimeOffset captureTime)
    {
        if (frequencyKhz is null || frequencyKhz <= 0)
            return Unknown();

        double? age = null;
        string confidence;
        if (updatedAt is not null)
        {
            age = Math.Max(0, (captureTime - updatedAt.Value).TotalSeconds);
            confidence = ClassifyConfidence(age);
        }
        else
        {
            // Have a frequency but no freshness timestamp — do not treat as Live.
            confidence = FrequencyConfidenceLevels.Stale;
        }

        var normalizedSource = NormalizeSource(source);
        return new FrequencySnapshot
        {
            FrequencyKhz = frequencyKhz,
            Source = normalizedSource,
            UpdatedAt = updatedAt,
            AgeSecondsAtCapture = age,
            Confidence = confidence
        };
    }

    public static string NormalizeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return FrequencySources.Unknown;
        if (source.Contains("CAT", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("rigctl", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Manual", StringComparison.OrdinalIgnoreCase))
            return FrequencySources.ManualCat;
        if (source.Contains("Trimode", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Winlink", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Configured", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("memory", StringComparison.OrdinalIgnoreCase))
            return FrequencySources.Winlink;
        return FrequencySources.Unknown;
    }
}

/// <summary>
/// One completed (or in-progress) RF transmission session detected from wattmeter forward power.
/// Bursty modes (PACTOR) coalesce multiple overs separated by less than SessionCoalesceMs
/// into a single session. Independent of Trimode/Winlink session state.
/// </summary>
public sealed class RfTransmissionEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    /// <summary>Wall-clock session duration including inter-burst gaps while coalescing.</summary>
    public double? DurationSeconds { get; set; }
    public bool InProgress { get; set; }

    /// <summary>Number of forward-power bursts merged into this session (PACTOR overs).</summary>
    public int BurstCount { get; set; } = 1;

    public decimal PeakForwardPowerWatts { get; set; }
    /// <summary>Max reflected power derived from SWR (not a direct LP-100A measurement).</summary>
    public decimal MaxReflectedPowerWatts { get; set; }
    public string MaxReflectedPowerSource { get; set; } = RfReflectedPowerSources.Calculated;

    /// <summary>
    /// Max SWR accepted only while forward power ≥ SwrMinForwardWatts (valid samples).
    /// Null when no valid SWR sample was seen.
    /// </summary>
    public decimal? MaxSwr { get; set; }
    public decimal? AverageSwr { get; set; }
    /// <summary>True when max/avg SWR sits at the LP-100A floor (exactly 1.00).</summary>
    public bool SwrAtResolutionFloor { get; set; }

    public decimal? StartFrequencyKhz { get; set; }
    public decimal? EndFrequencyKhz { get; set; }
    public string FrequencySource { get; set; } = FrequencySources.Unknown;
    public double? FrequencyAgeSecondsAtStart { get; set; }
    public string FrequencyConfidence { get; set; } = FrequencyConfidenceLevels.Unknown;
    public bool FrequencyChangedDuringTx { get; set; }
    public string? FrequencyNote { get; set; }
}

public static class RfReflectedPowerSources
{
    public const string Calculated = "calculated";
}
