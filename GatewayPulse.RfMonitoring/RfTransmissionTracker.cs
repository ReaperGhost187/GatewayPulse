namespace GatewayPulse.RfMonitoring;

/// <summary>
/// Detects RF transmissions from forward-power threshold crossings with end debounce.
/// Frequency snapshots are supplied by the caller at each sample.
/// </summary>
public sealed class RfTransmissionTracker
{
    private readonly decimal _txThresholdWatts;
    private readonly TimeSpan _endDebounce;
    private readonly decimal _frequencyChangeKhz;
    private RfTransmissionEvent? _active;
    private DateTimeOffset? _belowThresholdSince;
    private decimal _swrSum;
    private int _swrCount;

    public RfTransmissionTracker(
        decimal txThresholdWatts = 0.05m,
        TimeSpan? endDebounce = null,
        decimal frequencyChangeKhz = 0.1m)
    {
        _txThresholdWatts = Math.Max(0.001m, txThresholdWatts);
        _endDebounce = endDebounce ?? TimeSpan.FromMilliseconds(750);
        _frequencyChangeKhz = Math.Max(0.001m, frequencyChangeKhz);
    }

    public RfTransmissionEvent? Active => _active;

    /// <summary>
    /// Process one wattmeter sample. Returns a completed event when TX ends; otherwise null.
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
                PeakForwardPowerWatts = forwardWatts,
                MaxReflectedPowerWatts = reflectedWatts ?? 0m,
                MaxSwr = swr ?? 1m,
                AverageSwr = swr ?? 1m,
                StartFrequencyKhz = frequency.FrequencyKhz,
                EndFrequencyKhz = frequency.FrequencyKhz,
                FrequencySource = frequency.Source,
                FrequencyAgeSecondsAtStart = frequency.AgeSecondsAtCapture,
                FrequencyConfidence = frequency.Confidence,
                FrequencyNote = BuildFrequencyNote(frequency)
            };
            _swrSum = swr ?? 0m;
            _swrCount = swr.HasValue ? 1 : 0;
            _belowThresholdSince = null;
            return null;
        }

        // Active transmission
        if (forwardWatts > _active.PeakForwardPowerWatts)
            _active.PeakForwardPowerWatts = forwardWatts;
        if (reflectedWatts is decimal r && r > _active.MaxReflectedPowerWatts)
            _active.MaxReflectedPowerWatts = r;
        if (swr is decimal s)
        {
            if (s > _active.MaxSwr)
                _active.MaxSwr = s;
            _swrSum += s;
            _swrCount++;
            _active.AverageSwr = _swrCount > 0 ? _swrSum / _swrCount : s;
        }

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
            _belowThresholdSince = null;
            return null;
        }

        _belowThresholdSince ??= now;
        if (now - _belowThresholdSince.Value < _endDebounce)
            return null;

        var completed = _active;
        completed.InProgress = false;
        completed.EndTime = now;
        completed.DurationSeconds = Math.Max(0, (now - completed.StartTime).TotalSeconds);
        if (_swrCount > 0)
            completed.AverageSwr = _swrSum / _swrCount;
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
        _swrSum = 0;
        _swrCount = 0;
        return completed;
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
}
