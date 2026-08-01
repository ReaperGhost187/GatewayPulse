using GatewayPulse.RfMonitoring;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Watches RF telemetry for TX threshold crossings and records completed transmission events
/// with frequency snapshots. Does not require an active Winlink session.
/// </summary>
public sealed class RfTransmissionMonitor(
    IRfMonitor rfMonitor,
    FrequencySnapshotProvider frequencySnapshots,
    RfTransmissionHistoryStore historyStore,
    IOptionsMonitor<Lp100MonitorOptions> options,
    ILogger<RfTransmissionMonitor> logger) : BackgroundService
{
    private RfTransmissionTracker _tracker = new();
    private readonly object _gate = new();

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
                    delayMs = Math.Clamp(lp.IntervalMs, 200, 1000);
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
        var debounceMs = lp.TxEndDebounceMs > 0 ? lp.TxEndDebounceMs : 750;
        var forward = telemetry.ForwardPowerWatts ?? 0m;
        var reflected = telemetry.ReflectedPowerWatts;
        var swr = telemetry.Swr;
        var now = DateTimeOffset.UtcNow;

        FrequencySnapshot frequency;
        // Capture frequency on rising edge and while active (to detect mid-TX changes).
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
        lock (_gate)
        {
            // Recreate tracker when threshold/debounce settings change mid-run and idle.
            if (_tracker.Active is null)
                _tracker = new RfTransmissionTracker(threshold, TimeSpan.FromMilliseconds(debounceMs));

            completed = _tracker.Process(forward, reflected, swr, frequency, now);
        }

        if (completed is not null)
        {
            historyStore.Add(completed);
            logger.LogInformation(
                "RF transmission logged: {Duration:0.0}s peak {Peak:0.#}W max SWR {Swr:0.00} freq {Freq} ({Source}/{Confidence})",
                completed.DurationSeconds,
                completed.PeakForwardPowerWatts,
                completed.MaxSwr,
                completed.StartFrequencyKhz?.ToString("0.0") ?? "Unknown",
                completed.FrequencySource,
                completed.FrequencyConfidence);
        }
    }
}
