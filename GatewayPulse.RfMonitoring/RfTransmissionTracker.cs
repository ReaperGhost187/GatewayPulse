namespace GatewayPulse.RfMonitoring;

/// <summary>
/// Detects RF transmission sessions from forward-power threshold crossings with
/// session-coalesce timeout (PACTOR burst gap). Serial values are display snapshots,
/// not RF-envelope samples — peak hold on the meter is preferred for bursty modes.
/// </summary>
public sealed class RfTransmissionTracker
{
    /// <summary>
    /// Default SWR acceptance floor. Below ~0.5 W the LP-100A still reports SWR,
    /// but low-power coupler noise makes the value unreliable for session max/avg.
    /// </summary>
    public const decimal DefaultSwrMinForwardWatts = 0.5m;

    /// <summary>Default quiet gap that ends a coalesced PACTOR/session (ms).</summary>
    public const int DefaultSessionCoalesceMs = 6000;

    private readonly decimal _txThresholdWatts;
    private readonly TimeSpan _sessionCoalesce;
    private readonly decimal _swrMinForwardWatts;
    private readonly decimal _frequencyChangeKhz;
    private RfTransmissionEvent? _active;
    private DateTimeOffset? _belowThresholdSince;
    private bool _wasAbove;
    private decimal _swrSum;
    private int _swrCount;

    public RfTransmissionTracker(
        decimal txThresholdWatts = 0.05m,
        TimeSpan? sessionCoalesce = null,
        decimal frequencyChangeKhz = 0.1m,
        decimal swrMinForwardWatts = DefaultSwrMinForwardWatts)
    {
        _txThresholdWatts = Math.Max(0.001m, txThresholdWatts);
        _sessionCoalesce = sessionCoalesce ?? TimeSpan.FromMilliseconds(DefaultSessionCoalesceMs);
        _swrMinForwardWatts = Math.Max(0m, swrMinForwardWatts);
        _frequencyChangeKhz = Math.Max(0.001m, frequencyChangeKhz);
    }

    public RfTransmissionEvent? Active => _active;

    /// <summary>
    /// Process one wattmeter display snapshot. Returns a completed event when the
    /// coalesce quiet window expires; otherwise null. Gaps shorter than the coalesce
    /// timeout merge into the same session and increment <see cref="RfTransmissionEvent.BurstCount"/>.
    /// </summary>
    public RfTransmissionEvent? Process(
        decimal forwardWatts,
        decimal? reflectedWatts,
        decimal? swr,
        FrequencySnapshot frequency,
        DateTimeOffset now)
    {
        var above = forwardWatts > _txThresholdWatts;

        if (_active is null)
        {
            if (!above)
                return null;

            _active = new RfTransmissionEvent
            {
                StartTime = now,
                InProgress = true,
                BurstCount = 1,
                PeakForwardPowerWatts = forwardWatts,
                MaxReflectedPowerWatts = reflectedWatts ?? 0m,
                MaxReflectedPowerSource = RfReflectedPowerSources.Calculated,
                MaxSwr = null,
                AverageSwr = null,
                StartFrequencyKhz = frequency.FrequencyKhz,
                EndFrequencyKhz = frequency.FrequencyKhz,
                FrequencySource = frequency.Source,
                FrequencyAgeSecondsAtStart = frequency.AgeSecondsAtCapture,
                FrequencyConfidence = frequency.Confidence,
                FrequencyNote = BuildFrequencyNote(frequency)
            };
            _swrSum = 0m;
            _swrCount = 0;
            _belowThresholdSince = null;
            _wasAbove = true;
            ApplyValidSwr(_active, forwardWatts, swr, reflectedWatts);
            return null;
        }

        // Active session (may be in an inter-burst gap)
        if (forwardWatts > _active.PeakForwardPowerWatts)
            _active.PeakForwardPowerWatts = forwardWatts;

        ApplyValidSwr(_active, forwardWatts, swr, reflectedWatts);

        if (frequency.FrequencyKhz is decimal currentFreq &&
            _active.StartFrequencyKhz is decimal startFreq &&
            Math.Abs(currentFreq - startFreq) >= _frequencyChangeKhz)
        {
            _active.FrequencyChangedDuringTx = true;
            _active.EndFrequencyKhz = currentFreq;
            _active.FrequencyNote = BuildFrequencyNote(frequency) +
                " · Frequency changed during TX";
        }
        else if (!_active.FrequencyChangedDuringTx && frequency.FrequencyKhz is not null)
        {
            _active.EndFrequencyKhz = frequency.FrequencyKhz;
        }

        if (above)
        {
            // New burst after a quiet gap that has not yet ended the session.
            if (!_wasAbove)
                _active.BurstCount = Math.Max(1, _active.BurstCount + 1);
            _wasAbove = true;
            _belowThresholdSince = null;
            return null;
        }

        _wasAbove = false;
        _belowThresholdSince ??= now;
        if (now - _belowThresholdSince.Value < _sessionCoalesce)
            return null;

        var completed = _active;
        completed.InProgress = false;
        completed.EndTime = now;
        completed.DurationSeconds = Math.Max(0, (now - completed.StartTime).TotalSeconds);
        if (_swrCount > 0)
        {
            completed.AverageSwr = _swrSum / _swrCount;
            completed.SwrAtResolutionFloor = completed.MaxSwr is decimal max && max <= 1.00m;
        }

        if (completed.FrequencyChangedDuringTx)
        {
            var baseNote = BuildFrequencyNote(new FrequencySnapshot
            {
                FrequencyKhz = completed.StartFrequencyKhz,
                Source = completed.FrequencySource,
                AgeSecondsAtCapture = completed.FrequencyAgeSecondsAtStart,
                Confidence = completed.FrequencyConfidence
            });
            completed.FrequencyNote =
                $"{baseNote} · End frequency: {completed.EndFrequencyKhz:0.0} kHz · Frequency changed during TX";
        }

        _active = null;
        _belowThresholdSince = null;
        _wasAbove = false;
        _swrSum = 0;
        _swrCount = 0;
        return completed;
    }

    private void ApplyValidSwr(
        RfTransmissionEvent active,
        decimal forwardWatts,
        decimal? swr,
        decimal? reflectedWatts)
    {
        if (forwardWatts < _swrMinForwardWatts)
            return;

        if (swr is decimal s)
        {
            if (active.MaxSwr is null || s > active.MaxSwr)
                active.MaxSwr = s;
            _swrSum += s;
            _swrCount++;
            active.AverageSwr = _swrSum / _swrCount;
            active.SwrAtResolutionFloor = s <= 1.00m && (active.MaxSwr ?? s) <= 1.00m;
        }

        if (reflectedWatts is decimal r && r > active.MaxReflectedPowerWatts)
            active.MaxReflectedPowerWatts = r;
    }

    public static string BuildFrequencyNote(FrequencySnapshot snapshot)
    {
        if (snapshot.FrequencyKhz is null)
            return "Frequency: Unknown";

        var age = snapshot.AgeSecondsAtCapture is double a
            ? $"Age at TX start: {a:0} seconds"
            : "Age at TX start: unknown";
        return $"Frequency: {snapshot.FrequencyKhz.Value:0.0} kHz · Source: {snapshot.Source} · {age} · Confidence: {snapshot.Confidence}";
    }

    /// <summary>Format SWR for UI: exact 1.00 is the meter's resolution floor, not perfect match.</summary>
    public static string FormatSwrDisplay(decimal? swr, bool atFloor = false)
    {
        if (swr is null)
            return "—";
        if (atFloor || swr.Value <= 1.00m)
            return "≤1.00";
        return swr.Value.ToString("0.00");
    }
}
