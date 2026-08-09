namespace GatewayPulse.Core;

/// <summary>
/// Conservatively recognizes Trimode scan motion from already-polled CAT frequency samples.
/// This is recovery evidence only: callers must gate it on a current-session authoritative
/// "Scanning suspended" event. Four matched observations (three transitions) are required.
/// </summary>
internal sealed class CatScanMotionTracker
{
    internal const int MatchToleranceHz = 100;
    internal const int RequiredTransitions = 3;
    internal const int RequiredDistinctChannels = 2;
    internal static readonly TimeSpan MotionWindow = TimeSpan.FromSeconds(15);

    private string _channelKey = "";
    private DateTimeOffset? _lastTimestamp;
    private DateTimeOffset? _sequenceStartedAt;
    private int? _lastMatchedHz;
    private int _transitions;
    private readonly HashSet<int> _distinctChannels = [];

    public bool Recovered { get; private set; }

    public void Reset()
    {
        _channelKey = "";
        _lastTimestamp = null;
        ResetCandidate();
        Recovered = false;
    }

    public bool Observe(
        decimal frequencyKhz,
        DateTimeOffset observedAt,
        IReadOnlyCollection<ScanChannel> channels,
        DateTimeOffset now)
    {
        var configuredHz = SynchronizeChannels(channels);

        if (Recovered || configuredHz.Length < RequiredDistinctChannels)
            return Recovered;

        // Cache timestamps identify successful CAT reads. Ignore replayed, out-of-order,
        // future, and stale snapshots rather than treating endpoint reads as observations.
        if (_lastTimestamp.HasValue && observedAt <= _lastTimestamp.Value)
            return false;
        _lastTimestamp = observedAt;

        if (observedAt > now || now - observedAt > MotionWindow)
        {
            ResetCandidate();
            return false;
        }

        var frequencyHz = decimal.Round(frequencyKhz * 1000m, 0, MidpointRounding.AwayFromZero);
        if (frequencyHz <= 0 || frequencyHz > int.MaxValue)
        {
            ResetCandidate();
            return false;
        }

        var matchedHz = MatchConfiguredCenter(decimal.ToInt32(frequencyHz), configuredHz);

        // An off-list/manual tune invalidates the whole candidate sequence.
        if (!matchedHz.HasValue)
        {
            ResetCandidate();
            return false;
        }

        if (!_sequenceStartedAt.HasValue || observedAt - _sequenceStartedAt.Value > MotionWindow)
        {
            StartCandidate(matchedHz.Value, observedAt);
            return false;
        }

        // Polls commonly repeat the same frequency; only actual channel transitions count.
        if (_lastMatchedHz == matchedHz.Value)
            return false;

        _lastMatchedHz = matchedHz.Value;
        _transitions++;
        _distinctChannels.Add(matchedHz.Value);

        Recovered =
            _transitions >= RequiredTransitions &&
            _distinctChannels.Count >= RequiredDistinctChannels;
        return Recovered;
    }

    /// <summary>
    /// Maps a RadioCat/CI-V sample onto a configured Trimode center frequency.
    /// <para>
    /// <see cref="ScanChannel.FrequencyHz"/> is the Trimode INI PACTOR center/carrier.
    /// Dashboard overlay treats the RadioCat cache value as that same center basis
    /// (<c>CurrentFrequencyKhz</c> = cache, <c>DialFrequencyKhz</c> = center − 1.5 kHz).
    /// Some radios still report the USB dial instead, so also accept
    /// <see cref="PactorFrequency.DialToCenterHz"/> within the same tight tolerance.
    /// </para>
    /// </summary>
    internal static int? MatchConfiguredCenter(int observedHz, IReadOnlyList<int> configuredCenterHz)
    {
        int? best = null;
        long bestDelta = long.MaxValue;

        foreach (var centerHz in configuredCenterHz)
        {
            Consider(observedHz, centerHz, ref best, ref bestDelta);

            // Observation may be the radio dial for that center (center − 1.5 kHz).
            if (observedHz <= int.MaxValue - PactorFrequency.CenterToDialOffsetHz)
            {
                var asCenterFromDial = PactorFrequency.DialToCenterHz(observedHz);
                Consider(asCenterFromDial, centerHz, ref best, ref bestDelta);
            }
        }

        return best;

        static void Consider(int candidateHz, int centerHz, ref int? best, ref long bestDelta)
        {
            var delta = Math.Abs((long)candidateHz - centerHz);
            if (delta > MatchToleranceHz || delta >= bestDelta)
                return;

            bestDelta = delta;
            best = centerHz;
        }
    }

    public int[] SynchronizeChannels(IReadOnlyCollection<ScanChannel> channels)
    {
        var configuredHz = channels
            .Where(channel => channel.FrequencyHz > 0)
            .Select(channel => channel.FrequencyHz)
            .Distinct()
            .Order()
            .ToArray();
        var channelKey = string.Join(",", configuredHz);

        if (!string.Equals(channelKey, _channelKey, StringComparison.Ordinal))
        {
            _channelKey = channelKey;
            _lastTimestamp = null;
            ResetCandidate();
            Recovered = false;
        }

        return configuredHz;
    }

    private void StartCandidate(int matchedHz, DateTimeOffset observedAt)
    {
        ResetCandidate();
        _sequenceStartedAt = observedAt;
        _lastMatchedHz = matchedHz;
        _distinctChannels.Add(matchedHz);
    }

    private void ResetCandidate()
    {
        _sequenceStartedAt = null;
        _lastMatchedHz = null;
        _transitions = 0;
        _distinctChannels.Clear();
    }
}
