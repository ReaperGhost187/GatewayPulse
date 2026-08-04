using System.Globalization;
using System.Text.Json;

namespace GatewayPulse.RfMonitoring;

/// <summary>
/// One completed RF/PACTOR session observation for historical SWR-vs-frequency charts.
/// Only sessions with valid frequency and valid SWR are stored.
/// </summary>
public sealed class RfSwrByFrequencyObservation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? TransmissionId { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Transmit frequency in Hz.</summary>
    public long FrequencyHz { get; set; }

    public decimal? MaxSwr { get; set; }
    public decimal? AverageSwr { get; set; }
    public bool SwrAtResolutionFloor { get; set; }

    public decimal PeakForwardPowerWatts { get; set; }
    public decimal MaxReflectedPowerWatts { get; set; }
    public string ReflectedPowerSource { get; set; } = RfReflectedPowerSources.Calculated;

    public double? DurationSeconds { get; set; }
    public int BurstCount { get; set; } = 1;

    /// <summary>Winlink / CAT / Unknown channel-source label.</summary>
    public string FrequencySource { get; set; } = FrequencySources.Unknown;
    public string FrequencyConfidence { get; set; } = FrequencyConfidenceLevels.Unknown;
    public double? FrequencyAgeSecondsAtStart { get; set; }
}

/// <summary>
/// Bounded persistence of SWR-by-frequency observations from coalesced RF sessions.
/// Separate from the live RF Analysis timeline store.
/// </summary>
public sealed class RfSwrByFrequencyStore
{
    public const int DefaultMaxObservations = 20_000;
    /// <summary>Bucket width for aggregation (Hz). 100 Hz ≈ 0.1 kHz channel grouping.</summary>
    public const long DefaultBucketHz = 100;

    private readonly string _path;
    private readonly int _maxObservations;
    private readonly object _gate = new();
    private List<RfSwrByFrequencyObservation> _observations = [];
    private bool _loaded;

    public RfSwrByFrequencyStore(string path, int maxObservations = DefaultMaxObservations)
    {
        _path = Path.GetFullPath(path);
        _maxObservations = Math.Clamp(maxObservations, 500, 100_000);
    }

    public string FilePath => _path;

    /// <summary>
    /// Record a completed coalesced session when it has valid frequency and valid SWR.
    /// </summary>
    public bool TryAddFromSession(RfTransmissionEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (completed.InProgress)
            return false;

        var freqKhz = completed.EndFrequencyKhz ?? completed.StartFrequencyKhz;
        if (freqKhz is null or <= 0)
            return false;
        if (completed.MaxSwr is null && completed.AverageSwr is null)
            return false;
        if (completed.PeakForwardPowerWatts <= 0)
            return false;

        var observation = new RfSwrByFrequencyObservation
        {
            TransmissionId = completed.Id,
            Timestamp = completed.EndTime ?? completed.StartTime,
            FrequencyHz = (long)Math.Round(freqKhz.Value * 1000m, MidpointRounding.AwayFromZero),
            MaxSwr = completed.MaxSwr,
            AverageSwr = completed.AverageSwr,
            SwrAtResolutionFloor = completed.SwrAtResolutionFloor,
            PeakForwardPowerWatts = completed.PeakForwardPowerWatts,
            MaxReflectedPowerWatts = completed.MaxReflectedPowerWatts,
            ReflectedPowerSource = string.IsNullOrWhiteSpace(completed.MaxReflectedPowerSource)
                ? RfReflectedPowerSources.Calculated
                : completed.MaxReflectedPowerSource,
            DurationSeconds = completed.DurationSeconds,
            BurstCount = Math.Max(1, completed.BurstCount),
            FrequencySource = completed.FrequencySource ?? FrequencySources.Unknown,
            FrequencyConfidence = completed.FrequencyConfidence ?? FrequencyConfidenceLevels.Unknown,
            FrequencyAgeSecondsAtStart = completed.FrequencyAgeSecondsAtStart
        };

        Add(observation);
        return true;
    }

    public void Add(RfSwrByFrequencyObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            EnsureLoaded_NoLock();
            _observations.Insert(0, observation);
            if (_observations.Count > _maxObservations)
                _observations.RemoveRange(_maxObservations, _observations.Count - _maxObservations);
            Persist_NoLock();
        }
    }

    public object Query(
        string? range = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? source = null,
        string? confidence = null,
        long? minFrequencyHz = null,
        long? maxFrequencyHz = null,
        decimal? minForwardWatts = null,
        string metric = "max",
        bool aggregate = false,
        long bucketHz = DefaultBucketHz,
        string? compare = null)
    {
        lock (_gate)
        {
            EnsureLoaded_NoLock();
            ResolveWindow(range, from, to, out var start, out var end, out var resolvedRange);

            var filtered = Filter(
                _observations,
                start,
                end,
                source,
                confidence,
                minFrequencyHz,
                maxFrequencyHz,
                minForwardWatts);

            var metricKey = NormalizeMetric(metric);
            var points = filtered
                .Select(o => ToPoint(o, metricKey))
                .Where(p => p is not null)
                .Cast<object>()
                .ToList();

            object? aggregates = null;
            if (aggregate)
            {
                var width = Math.Max(1, bucketHz);
                aggregates = filtered
                    .GroupBy(o => o.FrequencyHz / width * width)
                    .OrderBy(g => g.Key)
                    .Select(g =>
                    {
                        var swrs = g
                            .Select(o => SelectSwr(o, metricKey))
                            .Where(v => v.HasValue)
                            .Select(v => v!.Value)
                            .OrderBy(v => v)
                            .ToList();
                        if (swrs.Count == 0)
                            return null;
                        return new
                        {
                            frequencyHz = g.Key + width / 2,
                            sampleCount = swrs.Count,
                            medianSwr = Median(swrs),
                            averageSwr = swrs.Average(),
                            worstSwr = swrs.Max(),
                            peakForwardWatts = g.Max(o => o.PeakForwardPowerWatts)
                        };
                    })
                    .Where(a => a is not null)
                    .ToList();
            }

            object? comparison = null;
            if (!string.IsNullOrWhiteSpace(compare))
                comparison = BuildComparison(
                    compare!,
                    source,
                    confidence,
                    minFrequencyHz,
                    maxFrequencyHz,
                    minForwardWatts,
                    metricKey,
                    bucketHz);

            var sources = _observations
                .Select(o => o.FrequencySource)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new
            {
                range = resolvedRange,
                from = start,
                to = end,
                metric = metricKey,
                aggregate,
                bucketHz = Math.Max(1, bucketHz),
                observationCount = filtered.Count,
                reflectedPowerSource = RfReflectedPowerSources.Calculated,
                sources,
                points,
                aggregates,
                comparison
            };
        }
    }

    private object? BuildComparison(
        string compare,
        string? source,
        string? confidence,
        long? minFrequencyHz,
        long? maxFrequencyHz,
        decimal? minForwardWatts,
        string metricKey,
        long bucketHz)
    {
        var now = DateTimeOffset.UtcNow;
        TimeSpan window;
        string label;
        switch (compare.Trim().ToLowerInvariant())
        {
            case "7d":
            case "7days":
            case "current7d":
                window = TimeSpan.FromDays(7);
                label = "7d";
                break;
            case "30d":
            case "30days":
            case "current30d":
                window = TimeSpan.FromDays(30);
                label = "30d";
                break;
            default:
                return null;
        }

        var currentStart = now - window;
        var previousStart = now - window - window;
        var previousEnd = currentStart;
        var width = Math.Max(1, bucketHz);

        var current = Filter(_observations, currentStart, now, source, confidence, minFrequencyHz, maxFrequencyHz, minForwardWatts);
        var previous = Filter(_observations, previousStart, previousEnd, source, confidence, minFrequencyHz, maxFrequencyHz, minForwardWatts);

        static Dictionary<long, List<decimal>> Bucket(IEnumerable<RfSwrByFrequencyObservation> items, long w, string metric)
        {
            var map = new Dictionary<long, List<decimal>>();
            foreach (var o in items)
            {
                var swr = SelectSwr(o, metric);
                if (swr is null) continue;
                var key = o.FrequencyHz / w * w;
                if (!map.TryGetValue(key, out var list))
                {
                    list = [];
                    map[key] = list;
                }
                list.Add(swr.Value);
            }
            return map;
        }

        var curMap = Bucket(current, width, metricKey);
        var prevMap = Bucket(previous, width, metricKey);
        var keys = curMap.Keys.Union(prevMap.Keys).OrderBy(k => k).ToList();

        var rows = keys.Select(key =>
        {
            curMap.TryGetValue(key, out var c);
            prevMap.TryGetValue(key, out var p);
            var curAvg = c is { Count: > 0 } ? c.Average() : (decimal?)null;
            var prevAvg = p is { Count: > 0 } ? p.Average() : (decimal?)null;
            decimal? delta = curAvg is decimal ca && prevAvg is decimal pa ? ca - pa : null;
            return new
            {
                frequencyHz = key + width / 2,
                currentSampleCount = c?.Count ?? 0,
                previousSampleCount = p?.Count ?? 0,
                currentAverageSwr = curAvg,
                previousAverageSwr = prevAvg,
                currentWorstSwr = c is { Count: > 0 } ? c.Max() : (decimal?)null,
                previousWorstSwr = p is { Count: > 0 } ? p.Max() : (decimal?)null,
                deltaAverageSwr = delta,
                gettingWorse = delta is decimal d && d > 0.05m
            };
        }).ToList();

        return new
        {
            mode = label,
            currentFrom = currentStart,
            currentTo = now,
            previousFrom = previousStart,
            previousTo = previousEnd,
            buckets = rows
        };
    }

    private static List<RfSwrByFrequencyObservation> Filter(
        IEnumerable<RfSwrByFrequencyObservation> sourceList,
        DateTimeOffset start,
        DateTimeOffset end,
        string? source,
        string? confidence,
        long? minFrequencyHz,
        long? maxFrequencyHz,
        decimal? minForwardWatts)
    {
        IEnumerable<RfSwrByFrequencyObservation> q = sourceList.Where(o =>
            o.Timestamp >= start &&
            o.Timestamp <= end &&
            o.FrequencyHz > 0 &&
            (o.MaxSwr is > 0 || o.AverageSwr is > 0) &&
            o.PeakForwardPowerWatts > 0);

        if (!string.IsNullOrWhiteSpace(source) &&
            !string.Equals(source, "all", StringComparison.OrdinalIgnoreCase))
        {
            q = q.Where(o => string.Equals(o.FrequencySource, source, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(confidence) &&
            !string.Equals(confidence, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(confidence, "live_recent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(confidence, "liverecent", StringComparison.OrdinalIgnoreCase))
            {
                q = q.Where(o =>
                    string.Equals(o.FrequencyConfidence, FrequencyConfidenceLevels.Live, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(o.FrequencyConfidence, FrequencyConfidenceLevels.Recent, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                q = q.Where(o => string.Equals(o.FrequencyConfidence, confidence, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (minFrequencyHz is long minHz)
            q = q.Where(o => o.FrequencyHz >= minHz);
        if (maxFrequencyHz is long maxHz)
            q = q.Where(o => o.FrequencyHz <= maxHz);
        if (minForwardWatts is decimal minFwd && minFwd > 0)
            q = q.Where(o => o.PeakForwardPowerWatts >= minFwd);

        return q.ToList();
    }

    private static void ResolveWindow(
        string? range,
        DateTimeOffset? from,
        DateTimeOffset? to,
        out DateTimeOffset start,
        out DateTimeOffset end,
        out string resolvedRange)
    {
        end = to ?? DateTimeOffset.UtcNow;
        resolvedRange = string.IsNullOrWhiteSpace(range) ? "30d" : range.Trim().ToLowerInvariant();

        if (from is DateTimeOffset f)
        {
            start = f;
            if (string.IsNullOrWhiteSpace(range) || resolvedRange == "custom")
                resolvedRange = "custom";
            return;
        }

        if (resolvedRange is "all" or "allhistory")
        {
            start = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            resolvedRange = "all";
            return;
        }

        start = resolvedRange switch
        {
            "24h" or "1d" => end - TimeSpan.FromHours(24),
            "48h" => end - TimeSpan.FromHours(48),
            "7d" => end - TimeSpan.FromDays(7),
            "10d" => end - TimeSpan.FromDays(10),
            "14d" => end - TimeSpan.FromDays(14),
            "20d" => end - TimeSpan.FromDays(20),
            "30d" => end - TimeSpan.FromDays(30),
            "40d" => end - TimeSpan.FromDays(40),
            "60d" => end - TimeSpan.FromDays(60),
            "80d" => end - TimeSpan.FromDays(80),
            "90d" => end - TimeSpan.FromDays(90),
            "160d" => end - TimeSpan.FromDays(160),
            "360d" => end - TimeSpan.FromDays(360),
            "5d" => end - TimeSpan.FromDays(5),
            "1h" => end - TimeSpan.FromHours(1),
            "6h" => end - TimeSpan.FromHours(6),
            "12h" => end - TimeSpan.FromHours(12),
            _ => end - TimeSpan.FromDays(30)
        };
    }

    private static string NormalizeMetric(string? metric) =>
        string.Equals(metric, "average", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(metric, "avg", StringComparison.OrdinalIgnoreCase)
            ? "average"
            : "max";

    private static decimal? SelectSwr(RfSwrByFrequencyObservation o, string metric) =>
        metric == "average" ? (o.AverageSwr ?? o.MaxSwr) : (o.MaxSwr ?? o.AverageSwr);

    private static object? ToPoint(RfSwrByFrequencyObservation o, string metric)
    {
        var swr = SelectSwr(o, metric);
        if (swr is null) return null;
        return new
        {
            id = o.Id,
            transmissionId = o.TransmissionId,
            t = o.Timestamp,
            frequencyHz = o.FrequencyHz,
            swr = swr.Value,
            maxSwr = o.MaxSwr,
            averageSwr = o.AverageSwr,
            swrAtResolutionFloor = o.SwrAtResolutionFloor || swr.Value <= 1.00m,
            peakForwardPowerWatts = o.PeakForwardPowerWatts,
            maxReflectedPowerWatts = o.MaxReflectedPowerWatts,
            reflectedPowerSource = o.ReflectedPowerSource,
            durationSeconds = o.DurationSeconds,
            burstCount = o.BurstCount,
            frequencySource = o.FrequencySource,
            frequencyConfidence = o.FrequencyConfidence,
            frequencyAgeSecondsAtStart = o.FrequencyAgeSecondsAtStart
        };
    }

    private static decimal Median(List<decimal> sorted)
    {
        if (sorted.Count == 0) return 0m;
        var mid = sorted.Count / 2;
        if (sorted.Count % 2 == 1)
            return sorted[mid];
        return (sorted[mid - 1] + sorted[mid]) / 2m;
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
            var file = JsonSerializer.Deserialize<RfSwrByFrequencyFile>(json, RfTelemetryJson.Options);
            _observations = file?.Observations ?? [];
        }
        catch
        {
            _observations = [];
        }
    }

    private void Persist_NoLock()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var payload = new RfSwrByFrequencyFile { Observations = _observations };
        var temporaryPath = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload, RfTelemetryJson.Options));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private sealed class RfSwrByFrequencyFile
    {
        public List<RfSwrByFrequencyObservation> Observations { get; set; } = [];
        public override string ToString() =>
            Observations.Count.ToString(CultureInfo.InvariantCulture);
    }
}
