using System.Text.Json;

namespace GatewayPulse.RfMonitoring;

public sealed class RfTransmissionHistoryStore
{
    public const int DefaultMaxEvents = 500;

    private readonly string _path;
    private readonly int _maxEvents;
    private readonly object _gate = new();
    private List<RfTransmissionEvent> _events = [];
    private bool _loaded;

    public RfTransmissionHistoryStore(string path, int maxEvents = DefaultMaxEvents)
    {
        _path = Path.GetFullPath(path);
        _maxEvents = Math.Clamp(maxEvents, 50, 5000);
    }

    public void Add(RfTransmissionEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        lock (_gate)
        {
            EnsureLoaded_NoLock();
            _events.Insert(0, completed);
            if (_events.Count > _maxEvents)
                _events.RemoveRange(_maxEvents, _events.Count - _maxEvents);
            Persist_NoLock();
        }
    }

    public IReadOnlyList<RfTransmissionEvent> List(int take = 50)
    {
        lock (_gate)
        {
            EnsureLoaded_NoLock();
            return _events.Take(Math.Clamp(take, 1, _maxEvents)).ToList();
        }
    }

    /// <summary>
    /// All completed sessions eligible for SWR-by-frequency analysis (valid freq + valid SWR).
    /// Newest first. Does not include in-progress sessions.
    /// </summary>
    public IReadOnlyList<RfTransmissionEvent> QuerySwrByFrequency(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? source = null,
        string? confidence = null,
        decimal? minForwardWatts = null,
        decimal? freqMinKhz = null,
        decimal? freqMaxKhz = null)
    {
        lock (_gate)
        {
            EnsureLoaded_NoLock();
            IEnumerable<RfTransmissionEvent> q = _events.Where(e =>
                !e.InProgress &&
                e.StartFrequencyKhz is > 0 &&
                e.MaxSwr is > 0 &&
                e.AverageSwr is > 0);

            if (from is not null)
                q = q.Where(e => e.StartTime >= from.Value);
            if (to is not null)
                q = q.Where(e => e.StartTime <= to.Value);

            if (!string.IsNullOrWhiteSpace(source) &&
                !string.Equals(source, "all", StringComparison.OrdinalIgnoreCase))
            {
                q = q.Where(e => string.Equals(e.FrequencySource, source, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(confidence) &&
                !string.Equals(confidence, "all", StringComparison.OrdinalIgnoreCase))
            {
                var allowed = confidence
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                q = q.Where(e => allowed.Contains(e.FrequencyConfidence));
            }

            if (minForwardWatts is > 0)
                q = q.Where(e => e.PeakForwardPowerWatts >= minForwardWatts.Value);
            if (freqMinKhz is not null)
                q = q.Where(e => e.StartFrequencyKhz >= freqMinKhz.Value);
            if (freqMaxKhz is not null)
                q = q.Where(e => e.StartFrequencyKhz <= freqMaxKhz.Value);

            return q.ToList();
        }
    }

    private void EnsureLoaded_NoLock()
    {
        if (_loaded)
            return;
        _loaded = true;
        if (!File.Exists(_path))
            return;
        try
        {
            var json = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize<RfTransmissionHistoryFile>(json, RfTelemetryJson.Options);
            _events = file?.Events ?? [];
        }
        catch
        {
            _events = [];
        }
    }

    private void Persist_NoLock()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var temporaryPath = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var payload = new RfTransmissionHistoryFile { Events = _events };
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload, RfTelemetryJson.Options));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private sealed class RfTransmissionHistoryFile
    {
        public List<RfTransmissionEvent> Events { get; set; } = [];
    }
}
