using System.Text.Json;

namespace GatewayPulse.RfMonitoring;

public static class RfAnalysisEventTypes
{
    public const string TxStart = "tx_start";
    public const string TxEnd = "tx_end";
    public const string FrequencyChange = "frequency_change";
    public const string HighSwr = "high_swr";
    public const string HighReflected = "high_reflected";
    public const string BatteryAlarm = "battery_alarm";
    public const string GatewayRestart = "gateway_restart";
    public const string WinlinkSessionStart = "winlink_session_start";
    public const string WinlinkSessionEnd = "winlink_session_end";
}

public sealed class RfAnalysisSample
{
    public DateTimeOffset Timestamp { get; set; }
    public decimal? ForwardPowerWatts { get; set; }
    public decimal? PeakForwardPowerWatts { get; set; }
    public decimal? ReflectedPowerWattsCalculated { get; set; }
    public decimal? Swr { get; set; }
    public bool SwrAtResolutionFloor { get; set; }
    public decimal? ReturnLossDb { get; set; }
    public decimal? FrequencyKhz { get; set; }
    public bool Transmitting { get; set; }
    public string? MeterMode { get; set; }
    public double? BatteryVoltage { get; set; }
    public double? BatteryCurrent { get; set; }
    public double? BatterySoc { get; set; }
}

public sealed class RfAnalysisEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; }
    public string Type { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? TransmissionId { get; set; }
}

/// <summary>
/// Bounded high-rate RF analysis history with progressive downsampling of older samples.
/// Fast sampling while transmitting; slower while idle.
/// </summary>
public sealed class RfAnalysisStore
{
    public const int DefaultTxSampleMs = 100;
    public const int DefaultIdleSampleMs = 1000;
    private const int MaxSamples = 60_000;
    private const int MaxEvents = 2_000;

    private readonly string _path;
    private readonly TimeSpan _txMinInterval;
    private readonly TimeSpan _idleMinInterval;
    private readonly object _gate = new();
    private List<RfAnalysisSample> _samples = [];
    private List<RfAnalysisEvent> _events = [];
    private DateTimeOffset _lastSampleAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastWrite = DateTimeOffset.MinValue;
    private bool _loaded;
    private decimal? _lastFrequencyKhz;

    public RfAnalysisStore(
        string path,
        TimeSpan? txMinInterval = null,
        TimeSpan? idleMinInterval = null)
    {
        _path = Path.GetFullPath(path);
        _txMinInterval = txMinInterval ?? TimeSpan.FromMilliseconds(DefaultTxSampleMs);
        _idleMinInterval = idleMinInterval ?? TimeSpan.FromMilliseconds(DefaultIdleSampleMs);
    }

    public string FilePath => _path;

    public void Record(
        RfTelemetry telemetry,
        decimal? frequencyKhz = null,
        double? batteryVoltage = null,
        double? batteryCurrent = null,
        double? batterySoc = null)
    {
        if (telemetry is null || !telemetry.Connected)
            return;

        var now = telemetry.LastUpdate == default ? DateTimeOffset.UtcNow : telemetry.LastUpdate;
        var minInterval = telemetry.Transmitting ? _txMinInterval : _idleMinInterval;

        lock (_gate)
        {
            EnsureLoaded_NoLock();
            if (_samples.Count > 0 && now - _lastSampleAt < minInterval)
                return;

            var freq = frequencyKhz;
            if (freq is decimal f &&
                _lastFrequencyKhz is decimal prev &&
                Math.Abs(f - prev) >= 0.1m)
            {
                AddEvent_NoLock(new RfAnalysisEvent
                {
                    Timestamp = now,
                    Type = RfAnalysisEventTypes.FrequencyChange,
                    Detail = $"Frequency {prev:0.0} → {f:0.0} kHz"
                });
            }

            if (freq is not null)
                _lastFrequencyKhz = freq;

            _samples.Add(new RfAnalysisSample
            {
                Timestamp = now,
                ForwardPowerWatts = telemetry.ForwardPowerWatts,
                PeakForwardPowerWatts = telemetry.PeakForwardPowerWatts ?? telemetry.LastPeakForwardPowerWatts,
                ReflectedPowerWattsCalculated =
                    telemetry.ReflectedPowerWattsCalculated ?? telemetry.ReflectedPowerWatts,
                Swr = telemetry.Swr,
                SwrAtResolutionFloor = telemetry.SwrAtResolutionFloor ||
                    RfDerivedMetrics.IsSwrAtResolutionFloor(telemetry.Swr),
                ReturnLossDb = telemetry.ReturnLossDb,
                FrequencyKhz = freq,
                Transmitting = telemetry.Transmitting,
                MeterMode = telemetry.MeterMode,
                BatteryVoltage = batteryVoltage,
                BatteryCurrent = batteryCurrent,
                BatterySoc = batterySoc
            });
            _lastSampleAt = now;

            TrimAndDownsample_NoLock();
            if (now - _lastWrite >= TimeSpan.FromSeconds(15))
                Persist_NoLock();
        }
    }

    public void AddEvent(RfAnalysisEvent analysisEvent)
    {
        lock (_gate)
        {
            EnsureLoaded_NoLock();
            AddEvent_NoLock(analysisEvent);
            Persist_NoLock();
        }
    }

    public object Query(DateTimeOffset? from, DateTimeOffset? to, string? range, string? transmissionId)
    {
        lock (_gate)
        {
            EnsureLoaded_NoLock();
            ResolveWindow(from, to, range, transmissionId, out var start, out var end, out var resolvedRange);

            var samples = _samples
                .Where(s => s.Timestamp >= start && s.Timestamp <= end)
                .ToList();
            var events = _events
                .Where(e => e.Timestamp >= start && e.Timestamp <= end)
                .OrderBy(e => e.Timestamp)
                .ToList();

            return new
            {
                range = resolvedRange,
                from = start,
                to = end,
                sampleCount = samples.Count,
                reflectedPowerSource = RfReflectedPowerSources.Calculated,
                series = new
                {
                    forward = Select(samples, s => s.ForwardPowerWatts),
                    peak = Select(samples, s => s.PeakForwardPowerWatts),
                    reflected = Select(samples, s => s.ReflectedPowerWattsCalculated),
                    swr = Select(samples, s => s.Swr),
                    returnLoss = Select(samples, s => s.ReturnLossDb),
                    frequency = Select(samples, s => s.FrequencyKhz),
                    batteryVoltage = Select(samples, s => s.BatteryVoltage is double v ? (decimal)v : null),
                    batteryCurrent = Select(samples, s => s.BatteryCurrent is double c ? (decimal)c : null),
                    batterySoc = Select(samples, s => s.BatterySoc is double soc ? (decimal)soc : null)
                },
                transmittingFlags = samples.Select(s => new { t = s.Timestamp, v = s.Transmitting }).ToList(),
                swrFloorFlags = samples
                    .Where(s => s.SwrAtResolutionFloor)
                    .Select(s => new { t = s.Timestamp, swr = s.Swr })
                    .ToList(),
                events
            };
        }
    }

    public object QueryEvents(DateTimeOffset? from, DateTimeOffset? to, string? range, int take = 200)
    {
        lock (_gate)
        {
            EnsureLoaded_NoLock();
            ResolveWindow(from, to, range, null, out var start, out var end, out _);
            var events = _events
                .Where(e => e.Timestamp >= start && e.Timestamp <= end)
                .OrderByDescending(e => e.Timestamp)
                .Take(Math.Clamp(take, 1, 1000))
                .ToList();
            return new { from = start, to = end, events };
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

    private void ResolveWindow(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? range,
        string? transmissionId,
        out DateTimeOffset start,
        out DateTimeOffset end,
        out string resolvedRange)
    {
        end = to ?? DateTimeOffset.UtcNow;
        resolvedRange = string.IsNullOrWhiteSpace(range) ? "custom" : range.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(transmissionId))
        {
            // Caller may pass last-tx window via from/to; otherwise fall back to 5 minutes.
            resolvedRange = "last";
        }

        if (from is DateTimeOffset f)
        {
            start = f;
            if (string.IsNullOrWhiteSpace(range))
                resolvedRange = "custom";
            return;
        }

        start = resolvedRange switch
        {
            "last" or "lasttx" or "last_tx" => end - TimeSpan.FromMinutes(5),
            "30s" => end - TimeSpan.FromSeconds(30),
            "5m" => end - TimeSpan.FromMinutes(5),
            "1h" => end - TimeSpan.FromHours(1),
            "24h" => end - TimeSpan.FromHours(24),
            "15m" => end - TimeSpan.FromMinutes(15),
            "custom" => end - TimeSpan.FromHours(1),
            _ => end - TimeSpan.FromHours(1)
        };
    }

    private static List<object> Select(List<RfAnalysisSample> samples, Func<RfAnalysisSample, decimal?> selector) =>
        samples
            .Select(s => new { t = s.Timestamp, v = selector(s) })
            .Where(p => p.v.HasValue)
            .Select(p => (object)new { t = p.t, v = p.v!.Value })
            .ToList();

    private void AddEvent_NoLock(RfAnalysisEvent analysisEvent)
    {
        _events.Add(analysisEvent);
        if (_events.Count > MaxEvents)
            _events.RemoveRange(0, _events.Count - MaxEvents);
    }

    private void TrimAndDownsample_NoLock()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(36);
        _samples = _samples.Where(s => s.Timestamp >= cutoff).ToList();

        // Progressive downsample: keep ~100ms in last 5m, ~1s in last hour, ~10s older.
        if (_samples.Count <= MaxSamples)
            return;

        var now = DateTimeOffset.UtcNow;
        var kept = new List<RfAnalysisSample>(_samples.Count);
        DateTimeOffset lastKept = DateTimeOffset.MinValue;
        foreach (var sample in _samples)
        {
            var age = now - sample.Timestamp;
            var minGap = age <= TimeSpan.FromMinutes(5) ? TimeSpan.FromMilliseconds(100)
                : age <= TimeSpan.FromHours(1) ? TimeSpan.FromSeconds(1)
                : TimeSpan.FromSeconds(10);
            if (sample.Transmitting || sample.Timestamp - lastKept >= minGap || kept.Count == 0)
            {
                kept.Add(sample);
                lastKept = sample.Timestamp;
            }
        }

        if (kept.Count > MaxSamples)
            kept.RemoveRange(0, kept.Count - MaxSamples);
        _samples = kept;
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
            var file = JsonSerializer.Deserialize<RfAnalysisFile>(json, RfTelemetryJson.Options);
            _samples = file?.Samples ?? [];
            _events = file?.Events ?? [];
        }
        catch
        {
            _samples = [];
            _events = [];
        }
    }

    private void Persist_NoLock()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var payload = new RfAnalysisFile { Samples = _samples, Events = _events };
        var temporaryPath = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload, RfTelemetryJson.Options));
        File.Move(temporaryPath, _path, overwrite: true);
        _lastWrite = DateTimeOffset.UtcNow;
    }

    private sealed class RfAnalysisFile
    {
        public List<RfAnalysisSample> Samples { get; set; } = [];
        public List<RfAnalysisEvent> Events { get; set; } = [];
    }
}
