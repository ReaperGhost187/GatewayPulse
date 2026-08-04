using GatewayPulse.RfMonitoring;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Watches RF telemetry for TX threshold crossings and records coalesced transmission sessions
/// with frequency snapshots. Does not require an active Winlink session.
/// </summary>
public sealed class RfTransmissionMonitor(
    IRfMonitor rfMonitor,
    FrequencySnapshotProvider frequencySnapshots,
    RfTransmissionHistoryStore historyStore,
    RfAnalysisStore analysisStore,
    RfSwrByFrequencyStore swrByFrequencyStore,
    IOptionsMonitor<Lp100MonitorOptions> options,
    ILogger<RfTransmissionMonitor> logger) : BackgroundService
{
    private RfTransmissionTracker _tracker = new();
    private readonly object _gate = new();
    private string? _activeId;
    private decimal? _lastAlertSwr;
    private decimal? _lastAlertReflected;

    public RfTransmissionEvent? ActiveTransmission
    {
        get { lock (_gate) return _tracker.Active; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var lp = options.CurrentValue;
            var delayMs = 500;
            try
            {
                if (lp.Enabled || string.Equals(
                        (await rfMonitor.GetTelemetryAsync()).Provider,
                        "mock",
                        StringComparison.OrdinalIgnoreCase))
                {
                    delayMs = Math.Clamp(lp.IntervalMs, 50, 1000);
                    await SampleAsync(lp, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "RF transmission sample skipped.");
            }

            try
            {
                await Task.Delay(delayMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SampleAsync(Lp100MonitorOptions lp, CancellationToken cancellationToken)
    {
        var telemetry = await rfMonitor.GetTelemetryAsync();
        if (!telemetry.Connected && !telemetry.Transmitting)
            return;

        var threshold = lp.TxThresholdWatts > 0 ? lp.TxThresholdWatts : 0.05m;
        var coalesceMs = lp.EffectiveSessionCoalesceMs;
        var swrMin = lp.SwrMinForwardWatts > 0
            ? lp.SwrMinForwardWatts
            : RfTransmissionTracker.DefaultSwrMinForwardWatts;
        var forward = telemetry.ForwardPowerWatts ?? 0m;
        var reflected = telemetry.ReflectedPowerWattsCalculated ?? telemetry.ReflectedPowerWatts;
        var swr = telemetry.Swr;
        var now = DateTimeOffset.UtcNow;

        FrequencySnapshot frequency;
        bool needFrequency;
        lock (_gate)
        {
            needFrequency = _tracker.Active is not null || forward > threshold;
        }

        if (needFrequency)
            frequency = await frequencySnapshots.CaptureAsync(cancellationToken);
        else
            frequency = FrequencySnapshot.Unknown();

        RfTransmissionEvent? completed;
        RfTransmissionEvent? started = null;
        lock (_gate)
        {
            var hadActive = _tracker.Active is not null;
            if (_tracker.Active is null)
            {
                _tracker = new RfTransmissionTracker(
                    threshold,
                    TimeSpan.FromMilliseconds(coalesceMs),
                    swrMinForwardWatts: swrMin);
            }

            completed = _tracker.Process(forward, reflected, swr, frequency, now);
            if (!hadActive && _tracker.Active is not null)
            {
                started = _tracker.Active;
                _activeId = started.Id;
            }

            if (completed is not null)
                _activeId = null;
        }

        if (started is not null)
        {
            analysisStore.AddEvent(new RfAnalysisEvent
            {
                Timestamp = started.StartTime,
                Type = RfAnalysisEventTypes.TxStart,
                Detail = $"RF session start · Peak {started.PeakForwardPowerWatts:0.##} W",
                TransmissionId = started.Id
            });
        }

        MaybeEmitThresholdEvents(lp, now, forward, swr, reflected);

        if (completed is not null)
        {
            historyStore.Add(completed);
            swrByFrequencyStore.TryAddFromSession(completed);
            analysisStore.AddEvent(new RfAnalysisEvent
            {
                Timestamp = completed.EndTime ?? now,
                Type = RfAnalysisEventTypes.TxEnd,
                Detail =
                    $"RF session end · {completed.BurstCount} burst(s) · {completed.DurationSeconds:0.0}s · " +
                    $"Peak {completed.PeakForwardPowerWatts:0.##} W · Max SWR {FormatSwr(completed)}",
                TransmissionId = completed.Id
            });
            logger.LogInformation(
                "RF session logged: {Duration:0.0}s bursts {Bursts} peak {Peak:0.#}W max SWR {Swr} freq {Freq} ({Source}/{Confidence})",
                completed.DurationSeconds,
                completed.BurstCount,
                completed.PeakForwardPowerWatts,
                FormatSwr(completed),
                completed.StartFrequencyKhz?.ToString("0.0") ?? "Unknown",
                completed.FrequencySource,
                completed.FrequencyConfidence);
        }
    }

    private void MaybeEmitThresholdEvents(
        Lp100MonitorOptions lp,
        DateTimeOffset now,
        decimal forward,
        decimal? swr,
        decimal? reflected)
    {
        var alerts = lp.Alerts ?? new Lp100AlertOptions();
        if (forward < (lp.SwrMinForwardWatts > 0 ? lp.SwrMinForwardWatts : 0.5m))
            return;

        if (swr is decimal s && s >= alerts.SwrWarning && (_lastAlertSwr is null || s > _lastAlertSwr + 0.05m))
        {
            _lastAlertSwr = s;
            analysisStore.AddEvent(new RfAnalysisEvent
            {
                Timestamp = now,
                Type = RfAnalysisEventTypes.HighSwr,
                Detail = $"SWR {RfTransmissionTracker.FormatSwrDisplay(s)} at {forward:0.##} W",
                TransmissionId = _activeId
            });
        }

        if (reflected is decimal r &&
            r >= alerts.ReflectedWarningWatts &&
            (_lastAlertReflected is null || r > _lastAlertReflected + 1m))
        {
            _lastAlertReflected = r;
            analysisStore.AddEvent(new RfAnalysisEvent
            {
                Timestamp = now,
                Type = RfAnalysisEventTypes.HighReflected,
                Detail = $"Reflected (calculated) {r:0.##} W",
                TransmissionId = _activeId
            });
        }

        if (forward <= (lp.TxThresholdWatts > 0 ? lp.TxThresholdWatts : 0.05m))
        {
            _lastAlertSwr = null;
            _lastAlertReflected = null;
        }
    }

    private static string FormatSwr(RfTransmissionEvent tx) =>
        RfTransmissionTracker.FormatSwrDisplay(tx.MaxSwr, tx.SwrAtResolutionFloor);
}
