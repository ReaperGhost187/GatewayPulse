using System.Globalization;
using System.Text.Json;

namespace GatewayPulse.RfMonitoring;

public sealed class RfHistoryStore
{
    public const int DefaultMinSampleSeconds = 5;
    private const int MaxSamples = 20_000;

    private readonly string _path;
    private readonly TimeSpan _minSampleInterval;
    private readonly object _gate = new();
    private List<RfHistorySample> _samples = [];
    private DateTimeOffset _lastWrite = DateTimeOffset.MinValue;
    private bool _loaded;

    public RfHistoryStore(string path, TimeSpan? minSampleInterval = null)
    {
        _path = Path.GetFullPath(path);
        _minSampleInterval = minSampleInterval ?? TimeSpan.FromSeconds(DefaultMinSampleSeconds);
    }

    public void Record(RfTelemetry telemetry)
    {
        if (telemetry is null || !telemetry.Connected || telemetry.Stale)
            return;

        var now = telemetry.LastUpdate == default ? DateTimeOffset.UtcNow : telemetry.LastUpdate;
        lock (_gate)
        {
            EnsureLoaded_NoLock();
            if (_samples.Count > 0 && now - _samples[^1].Timestamp < _minSampleInterval)
                return;

            _samples.Add(new RfHistorySample
            {
                Timestamp = now,
                ForwardPowerWatts = telemetry.ForwardPowerWatts,
                PeakForwardPowerWatts = telemetry.PeakForwardPowerWatts ?? telemetry.LastPeakForwardPowerWatts,
                ReflectedPowerWatts = telemetry.ReflectedPowerWatts,
                Swr = telemetry.Swr,
                Transmitting = telemetry.Transmitting
            });

            if (_samples.Count > MaxSamples)
                _samples.RemoveRange(0, _samples.Count - MaxSamples);

            if (now - _lastWrite >= TimeSpan.FromSeconds(30))
                Persist_NoLock();
        }
    }

    public object Query(string metric, string range)
    {
        if (!TryParseMetric(metric, out var parsedMetric))
            throw new ArgumentException("Unsupported metric.", nameof(metric));
        if (!TryParseRange(range, out var window))
            throw new ArgumentException("Unsupported range.", nameof(range));

        var cutoff = DateTimeOffset.UtcNow - window;
        lock (_gate)
        {
            EnsureLoaded_NoLock();
            var points = _samples
                .Where(s => s.Timestamp >= cutoff)
                .Select(s => new
                {
                    t = s.Timestamp,
                    v = SelectMetric(s, parsedMetric)
                })
                .Where(p => p.v.HasValue)
                .Select(p => new { t = p.t, v = p.v!.Value })
                .ToList();

            return new
            {
                metric = parsedMetric,
                range,
                points
            };
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            EnsureLoaded_NoLock();
            Persist_NoLock();
        }
    }

    public static bool TryParseMetric(string metric, out string normalized)
    {
        normalized = metric.Trim().ToLowerInvariant();
        return normalized is "forward" or "peak" or "reflected" or "swr";
    }

    public static bool TryParseRange(string range, out TimeSpan window)
    {
        window = range.Trim().ToLowerInvariant() switch
        {
            "15m" => TimeSpan.FromMinutes(15),
            "1h" => TimeSpan.FromHours(1),
            "6h" => TimeSpan.FromHours(6),
            "24h" => TimeSpan.FromHours(24),
            "7d" => TimeSpan.FromDays(7),
            _ => TimeSpan.Zero
        };
        return window > TimeSpan.Zero;
    }

    private static decimal? SelectMetric(RfHistorySample sample, string metric) => metric switch
    {
        "forward" => sample.ForwardPowerWatts,
        "peak" => sample.PeakForwardPowerWatts,
        "reflected" => sample.ReflectedPowerWatts,
        "swr" => sample.Swr,
        _ => null
    };

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
            var file = JsonSerializer.Deserialize<RfHistoryFile>(json, RfTelemetryJson.Options);
            _samples = file?.Samples ?? [];
        }
        catch
        {
            _samples = [];
        }
    }

    private void Persist_NoLock()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var payload = new RfHistoryFile { Samples = _samples };
        var temporaryPath = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload, RfTelemetryJson.Options));
        File.Move(temporaryPath, _path, overwrite: true);
        _lastWrite = DateTimeOffset.UtcNow;
    }

    private sealed class RfHistoryFile
    {
        public List<RfHistorySample> Samples { get; set; } = [];
    }

    private sealed class RfHistorySample
    {
        public DateTimeOffset Timestamp { get; set; }
        public decimal? ForwardPowerWatts { get; set; }
        public decimal? PeakForwardPowerWatts { get; set; }
        public decimal? ReflectedPowerWatts { get; set; }
        public decimal? Swr { get; set; }
        public bool Transmitting { get; set; }

        public override string ToString() =>
            Timestamp.ToString("O", CultureInfo.InvariantCulture);
    }
}
