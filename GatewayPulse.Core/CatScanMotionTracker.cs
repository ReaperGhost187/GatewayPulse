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

        var observedHz = decimal.ToInt32(frequencyHz);
        var matchedHz = configuredHz
            .Select(channelHz => new { ChannelHz = channelHz, Delta = Math.Abs((long)channelHz - observedHz) })
            .Where(candidate => candidate.Delta <= MatchToleranceHz)
            .OrderBy(candidate => candidate.Delta)
            .Select(candidate => (int?)candidate.ChannelHz)
            .FirstOrDefault();

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
