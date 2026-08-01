using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GatewayPulse.PowerMonitoring;

public enum PowerHistoryMetric
{
    StateOfCharge,
    Voltage,
    Current,
    Watts,
    RuntimeMinutes
}

public enum PowerHistoryRange
{
    OneHour,
    SixHours,
    TwelveHours,
    OneDay,
    TwoDays,
    FiveDays,
    TenDays,
    FourteenDays,
    TwentyDays,
    ThirtyDays,
    FortyDays,
    SixtyDays,
    EightyDays,
    NinetyDays,
    OneHundredSixtyDays,
    ThreeHundredSixtyDays
}

public sealed class PowerHistorySample
{
    public DateTimeOffset Timestamp { get; set; }
    public double? StateOfCharge { get; set; }
    public double? Voltage { get; set; }
    public double? Current { get; set; }
    public double? Watts { get; set; }
    public double? RuntimeMinutes { get; set; }
}

public sealed class PowerHistoryPoint
{
    public DateTimeOffset Timestamp { get; set; }
    public double? Value { get; set; }
}

public sealed class PowerHistoryQueryResult
{
    public string Metric { get; set; } = string.Empty;
    public string Range { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public IReadOnlyList<PowerHistoryPoint> Points { get; set; } = Array.Empty<PowerHistoryPoint>();
}

/// <summary>
/// Efficient rolling power-history store. Keeps up to 360 days of samples on disk
/// and in memory so charts survive service/browser restarts without slowing the live dashboard.
/// Older samples are progressively compacted to control file size.
/// </summary>
public sealed class PowerHistoryStore
{
    public const int DefaultMinSampleSeconds = 30;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(360);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly string _path;
    private readonly TimeSpan _minSampleInterval;
    private readonly object _sync = new();
    private readonly List<PowerHistorySample> _samples = new();
    private DateTimeOffset _lastSampleAt = DateTimeOffset.MinValue;
    private bool _loaded;

    public PowerHistoryStore(string path, TimeSpan? minSampleInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _minSampleInterval = minSampleInterval ?? TimeSpan.FromSeconds(DefaultMinSampleSeconds);
        if (_minSampleInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minSampleInterval));
    }

    public string FilePath => _path;

    public void Record(PowerTelemetry telemetry, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        var timestamp = now ?? telemetry.UpdatedAt ?? telemetry.LastUpdate;
        if (timestamp == default)
            timestamp = DateTimeOffset.UtcNow;

        var sample = new PowerHistorySample
        {
            Timestamp = timestamp.ToUniversalTime(),
            StateOfCharge = ToDouble(telemetry.System?.StateOfCharge ?? telemetry.StateOfCharge),
            Voltage = ToDouble(telemetry.System?.Voltage ?? telemetry.Voltage),
            Current = ToDouble(telemetry.System?.Current ?? telemetry.Current),
            Watts = ToDouble(telemetry.System?.Watts),
            RuntimeMinutes = ToDouble(telemetry.System?.TimeRemainingMinutes ?? telemetry.EstimatedRuntimeMinutes)
        };

        if (sample.StateOfCharge is null &&
            sample.Voltage is null &&
            sample.Current is null &&
            sample.Watts is null &&
            sample.RuntimeMinutes is null)
        {
            return;
        }

        lock (_sync)
        {
            EnsureLoaded_NoLock();
            if (_lastSampleAt != DateTimeOffset.MinValue &&
                sample.Timestamp - _lastSampleAt < _minSampleInterval)
            {
                return;
            }

            if (_samples.Count > 0)
            {
                var previous = _samples[^1];
                if (ValuesEqual(previous, sample) &&
                    sample.Timestamp - previous.Timestamp < TimeSpan.FromMinutes(5))
                {
                    return;
                }
            }

            _samples.Add(sample);
            _lastSampleAt = sample.Timestamp;
            Prune_NoLock(sample.Timestamp);
            Compact_NoLock(sample.Timestamp);
            Persist_NoLock();
        }
    }

    public PowerHistoryQueryResult Query(
        PowerHistoryMetric metric,
        PowerHistoryRange range,
        DateTimeOffset? now = null)
    {
        var clock = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var window = RangeToWindow(range);
        var from = clock - window;
        List<PowerHistoryPoint> points;

        lock (_sync)
        {
            EnsureLoaded_NoLock();
            points = _samples
                .Where(sample => sample.Timestamp >= from && sample.Timestamp <= clock)
                .Select(sample => new PowerHistoryPoint
                {
                    Timestamp = sample.Timestamp,
                    Value = SelectMetric(sample, metric)
                })
                .Where(point => point.Value.HasValue)
                .ToList();
        }

        // Downsample dense ranges so the browser chart stays light.
        points = Downsample(points, MaxPointsForRange(range));

        return new PowerHistoryQueryResult
        {
            Metric = MetricKey(metric),
            Range = RangeKey(range),
            Unit = MetricUnit(metric),
            Label = MetricLabel(metric),
            Points = points
        };
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                EnsureLoaded_NoLock();
                return _samples.Count;
            }
        }
    }

    public static bool TryParseMetric(string? value, out PowerHistoryMetric metric)
    {
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "soc":
            case "stateofcharge":
            case "state-of-charge":
            case "battery":
            case "batterylife":
                metric = PowerHistoryMetric.StateOfCharge;
                return true;
            case "v":
            case "voltage":
                metric = PowerHistoryMetric.Voltage;
                return true;
            case "i":
            case "a":
            case "current":
            case "amps":
                metric = PowerHistoryMetric.Current;
                return true;
            case "w":
            case "watts":
            case "power":
                metric = PowerHistoryMetric.Watts;
                return true;
            case "rt":
            case "runtime":
            case "remaining":
            case "runtimeminutes":
                metric = PowerHistoryMetric.RuntimeMinutes;
                return true;
            default:
                metric = default;
                return false;
        }
    }

    public static bool TryParseRange(string? value, out PowerHistoryRange range)
    {
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "1h":
            case "1hr":
            case "hour":
            case "1hour":
                range = PowerHistoryRange.OneHour;
                return true;
            case "6h":
            case "6hr":
            case "6hours":
                range = PowerHistoryRange.SixHours;
                return true;
            case "12h":
            case "12hr":
            case "12hours":
                range = PowerHistoryRange.TwelveHours;
                return true;
            case "24h":
            case "1d":
            case "day":
            case "24hours":
                range = PowerHistoryRange.OneDay;
                return true;
            case "48h":
            case "2d":
            case "48hours":
            case "2days":
                range = PowerHistoryRange.TwoDays;
                return true;
            case "5d":
            case "5day":
            case "5days":
                range = PowerHistoryRange.FiveDays;
                return true;
            case "10d":
            case "10":
            case "10day":
            case "10days":
                range = PowerHistoryRange.TenDays;
                return true;
            case "20d":
            case "20":
            case "20day":
            case "20days":
                range = PowerHistoryRange.TwentyDays;
                return true;
            case "40d":
            case "40":
            case "40day":
            case "40days":
                range = PowerHistoryRange.FortyDays;
                return true;
            case "80d":
            case "80":
            case "80day":
            case "80days":
                range = PowerHistoryRange.EightyDays;
                return true;
            case "14d":
            case "14":
            case "14day":
            case "14days":
                range = PowerHistoryRange.FourteenDays;
                return true;
            case "30d":
            case "30":
            case "30day":
            case "30days":
                range = PowerHistoryRange.ThirtyDays;
                return true;
            case "60d":
            case "60":
            case "60day":
            case "60days":
                range = PowerHistoryRange.SixtyDays;
                return true;
            case "90d":
            case "90":
            case "90day":
            case "90days":
                range = PowerHistoryRange.NinetyDays;
                return true;
            case "160d":
            case "160":
            case "160day":
            case "160days":
                range = PowerHistoryRange.OneHundredSixtyDays;
                return true;
            case "360d":
            case "360":
            case "360day":
            case "360days":
            case "1y":
            case "year":
                range = PowerHistoryRange.ThreeHundredSixtyDays;
                return true;
            // Legacy aliases from the first chart release.
            case "7d":
            case "7day":
            case "7days":
            case "week":
                range = PowerHistoryRange.FiveDays;
                return true;
            default:
                range = default;
                return false;
        }
    }

    public static string MetricKey(PowerHistoryMetric metric) => metric switch
    {
        PowerHistoryMetric.StateOfCharge => "soc",
        PowerHistoryMetric.Voltage => "voltage",
        PowerHistoryMetric.Current => "current",
        PowerHistoryMetric.Watts => "watts",
        PowerHistoryMetric.RuntimeMinutes => "runtime",
        _ => "soc"
    };

    public static string RangeKey(PowerHistoryRange range) => range switch
    {
        PowerHistoryRange.OneHour => "1h",
        PowerHistoryRange.SixHours => "6h",
        PowerHistoryRange.TwelveHours => "12h",
        PowerHistoryRange.OneDay => "24h",
        PowerHistoryRange.TwoDays => "48h",
        PowerHistoryRange.FiveDays => "5d",
        PowerHistoryRange.TenDays => "10d",
        PowerHistoryRange.FourteenDays => "14d",
        PowerHistoryRange.TwentyDays => "20d",
        PowerHistoryRange.ThirtyDays => "30d",
        PowerHistoryRange.FortyDays => "40d",
        PowerHistoryRange.SixtyDays => "60d",
        PowerHistoryRange.EightyDays => "80d",
        PowerHistoryRange.NinetyDays => "90d",
        PowerHistoryRange.OneHundredSixtyDays => "160d",
        PowerHistoryRange.ThreeHundredSixtyDays => "360d",
        _ => "24h"
    };

    public static string MetricLabel(PowerHistoryMetric metric) => metric switch
    {
        PowerHistoryMetric.StateOfCharge => "State of charge",
        PowerHistoryMetric.Voltage => "Battery voltage",
        PowerHistoryMetric.Current => "Battery current",
        PowerHistoryMetric.Watts => "Battery power",
        PowerHistoryMetric.RuntimeMinutes => "Estimated runtime",
        _ => "State of charge"
    };

    public static string MetricUnit(PowerHistoryMetric metric) => metric switch
    {
        PowerHistoryMetric.StateOfCharge => "%",
        PowerHistoryMetric.Voltage => "V",
        PowerHistoryMetric.Current => "A",
        PowerHistoryMetric.Watts => "W",
        PowerHistoryMetric.RuntimeMinutes => "min",
        _ => ""
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
            if (string.IsNullOrWhiteSpace(json))
                return;

            var document = JsonSerializer.Deserialize<HistoryFile>(json, JsonOptions);
            if (document?.Samples is null || document.Samples.Count == 0)
                return;

            var cutoff = DateTimeOffset.UtcNow - Retention;
            foreach (var sample in document.Samples
                         .Where(item => item.Timestamp >= cutoff)
                         .OrderBy(item => item.Timestamp))
            {
                _samples.Add(Normalize(sample));
            }

            if (_samples.Count > 0)
                _lastSampleAt = _samples[^1].Timestamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _samples.Clear();
            _lastSampleAt = DateTimeOffset.MinValue;
        }
    }

    private void Prune_NoLock(DateTimeOffset now)
    {
        var cutoff = now.ToUniversalTime() - Retention;
        if (_samples.Count == 0)
            return;

        var firstKeep = _samples.FindIndex(sample => sample.Timestamp >= cutoff);
        if (firstKeep <= 0)
            return;

        _samples.RemoveRange(0, firstKeep);
    }

    /// <summary>
    /// Progressively thins older samples so 360-day retention stays practical on disk.
    /// Recent 48 hours stay at full sample density.
    /// </summary>
    private void Compact_NoLock(DateTimeOffset now)
    {
        if (_samples.Count < 2)
            return;

        var clock = now.ToUniversalTime();
        var compacted = new List<PowerHistorySample>(_samples.Count);
        DateTimeOffset? lastKept = null;

        foreach (var sample in _samples)
        {
            var age = clock - sample.Timestamp;
            var minGap = age switch
            {
                var value when value <= TimeSpan.FromHours(48) => TimeSpan.Zero,
                var value when value <= TimeSpan.FromDays(10) => TimeSpan.FromMinutes(5),
                var value when value <= TimeSpan.FromDays(40) => TimeSpan.FromMinutes(15),
                var value when value <= TimeSpan.FromDays(90) => TimeSpan.FromMinutes(30),
                var value when value <= TimeSpan.FromDays(160) => TimeSpan.FromHours(1),
                _ => TimeSpan.FromHours(3)
            };

            if (minGap == TimeSpan.Zero ||
                lastKept is null ||
                sample.Timestamp - lastKept.Value >= minGap ||
                sample == _samples[^1])
            {
                compacted.Add(sample);
                lastKept = sample.Timestamp;
            }
        }

        if (compacted.Count == _samples.Count)
            return;

        _samples.Clear();
        _samples.AddRange(compacted);
    }

    private void Persist_NoLock()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var payload = new HistoryFile { Samples = _samples };
        var temporaryPath = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static PowerHistorySample Normalize(PowerHistorySample sample) => new()
    {
        Timestamp = sample.Timestamp.ToUniversalTime(),
        StateOfCharge = sample.StateOfCharge,
        Voltage = sample.Voltage,
        Current = sample.Current,
        Watts = sample.Watts,
        RuntimeMinutes = sample.RuntimeMinutes
    };

    private static double? SelectMetric(PowerHistorySample sample, PowerHistoryMetric metric) => metric switch
    {
        PowerHistoryMetric.StateOfCharge => sample.StateOfCharge,
        PowerHistoryMetric.Voltage => sample.Voltage,
        PowerHistoryMetric.Current => sample.Current,
        PowerHistoryMetric.Watts => sample.Watts,
        PowerHistoryMetric.RuntimeMinutes => sample.RuntimeMinutes,
        _ => null
    };

    private static TimeSpan RangeToWindow(PowerHistoryRange range) => range switch
    {
        PowerHistoryRange.OneHour => TimeSpan.FromHours(1),
        PowerHistoryRange.SixHours => TimeSpan.FromHours(6),
        PowerHistoryRange.TwelveHours => TimeSpan.FromHours(12),
        PowerHistoryRange.OneDay => TimeSpan.FromHours(24),
        PowerHistoryRange.TwoDays => TimeSpan.FromHours(48),
        PowerHistoryRange.FiveDays => TimeSpan.FromDays(5),
        PowerHistoryRange.TenDays => TimeSpan.FromDays(10),
        PowerHistoryRange.FourteenDays => TimeSpan.FromDays(14),
        PowerHistoryRange.TwentyDays => TimeSpan.FromDays(20),
        PowerHistoryRange.ThirtyDays => TimeSpan.FromDays(30),
        PowerHistoryRange.FortyDays => TimeSpan.FromDays(40),
        PowerHistoryRange.SixtyDays => TimeSpan.FromDays(60),
        PowerHistoryRange.EightyDays => TimeSpan.FromDays(80),
        PowerHistoryRange.NinetyDays => TimeSpan.FromDays(90),
        PowerHistoryRange.OneHundredSixtyDays => TimeSpan.FromDays(160),
        PowerHistoryRange.ThreeHundredSixtyDays => TimeSpan.FromDays(360),
        _ => TimeSpan.FromHours(24)
    };

    private static int MaxPointsForRange(PowerHistoryRange range) => range switch
    {
        PowerHistoryRange.OneHour => 240,
        PowerHistoryRange.SixHours => 360,
        PowerHistoryRange.TwelveHours => 420,
        PowerHistoryRange.OneDay => 480,
        PowerHistoryRange.TwoDays => 540,
        PowerHistoryRange.FiveDays => 600,
        PowerHistoryRange.TenDays => 700,
        PowerHistoryRange.FourteenDays => 750,
        PowerHistoryRange.TwentyDays => 800,
        PowerHistoryRange.ThirtyDays => 850,
        PowerHistoryRange.FortyDays => 900,
        PowerHistoryRange.SixtyDays => 950,
        PowerHistoryRange.EightyDays => 1000,
        PowerHistoryRange.NinetyDays => 1050,
        PowerHistoryRange.OneHundredSixtyDays => 1200,
        PowerHistoryRange.ThreeHundredSixtyDays => 1400,
        _ => 480
    };

    private static List<PowerHistoryPoint> Downsample(List<PowerHistoryPoint> points, int maxPoints)
    {
        if (points.Count <= maxPoints)
            return points;

        var result = new List<PowerHistoryPoint>(maxPoints);
        var step = (double)(points.Count - 1) / (maxPoints - 1);
        for (var index = 0; index < maxPoints; index++)
        {
            var sourceIndex = (int)Math.Round(index * step);
            result.Add(points[Math.Clamp(sourceIndex, 0, points.Count - 1)]);
        }

        return result;
    }

    private static bool ValuesEqual(PowerHistorySample left, PowerHistorySample right) =>
        NearlyEqual(left.StateOfCharge, right.StateOfCharge) &&
        NearlyEqual(left.Voltage, right.Voltage) &&
        NearlyEqual(left.Current, right.Current) &&
        NearlyEqual(left.Watts, right.Watts) &&
        NearlyEqual(left.RuntimeMinutes, right.RuntimeMinutes);

    private static bool NearlyEqual(double? left, double? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        return Math.Abs(left.Value - right.Value) < 0.0005;
    }

    private static double? ToDouble(decimal? value) =>
        value is null ? null : Convert.ToDouble(value.Value, CultureInfo.InvariantCulture);

    private static double? ToDouble(int? value) =>
        value is null ? null : value.Value;

    private sealed class HistoryFile
    {
        public List<PowerHistorySample> Samples { get; set; } = [];
    }
}
