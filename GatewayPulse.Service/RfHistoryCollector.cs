using GatewayPulse.PowerMonitoring;
using GatewayPulse.RfMonitoring;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Samples RF telemetry into chart history and high-rate analysis store.
/// Speeds up while transmitting so PACTOR sessions keep up with the collector.
/// </summary>
public sealed class RfHistoryCollector(
    IRfMonitor rfMonitor,
    IPowerMonitor powerMonitor,
    FrequencySnapshotProvider frequencySnapshots,
    RfHistoryStore historyStore,
    RfAnalysisStore analysisStore,
    IOptionsMonitor<Lp100MonitorOptions> options,
    ILogger<RfHistoryCollector> logger) : BackgroundService
{
    private DateTimeOffset _lastBatteryAlarm = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delayMs = 1000;
            try
            {
                if (options.CurrentValue.HistoryEnabled)
                {
                    var telemetry = await rfMonitor.GetTelemetryAsync();
                    historyStore.Record(telemetry);

                    decimal? freq = null;
                    if (telemetry.Transmitting || telemetry.Connected)
                    {
                        try
                        {
                            var snap = await frequencySnapshots.CaptureAsync(stoppingToken);
                            freq = snap.FrequencyKhz;
                        }
                        catch
                        {
                            // Frequency is optional for analysis samples.
                        }
                    }

                    double? voltage = null, current = null, soc = null;
                    try
                    {
                        var power = await powerMonitor.GetTelemetryAsync();
                        if (power is not null)
                        {
                            voltage = power.System?.Voltage is decimal v ? (double)v
                                : power.Voltage is decimal pv ? (double)pv : null;
                            current = power.System?.Current is decimal c ? (double)c
                                : power.Current is decimal pc ? (double)pc : null;
                            soc = power.System?.StateOfCharge is decimal s ? (double)s
                                : power.StateOfCharge is decimal ps ? (double)ps : null;

                            var alarm = power.Alarm == true || power.System?.Alarm == true;
                            var status = power.System?.Status;
                            if (alarm ||
                                string.Equals(status, "Critical", StringComparison.OrdinalIgnoreCase))
                            {
                                var now = DateTimeOffset.UtcNow;
                                if (now - _lastBatteryAlarm > TimeSpan.FromMinutes(2))
                                {
                                    _lastBatteryAlarm = now;
                                    analysisStore.AddEvent(new RfAnalysisEvent
                                    {
                                        Timestamp = now,
                                        Type = RfAnalysisEventTypes.BatteryAlarm,
                                        Detail = power.AlarmReason ?? power.System?.AlarmReason ?? status ?? "Battery alarm"
                                    });
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Power telemetry optional.
                    }

                    analysisStore.Record(telemetry, freq, voltage, current, soc);

                    delayMs = telemetry.Transmitting
                        ? Math.Clamp(options.CurrentValue.IntervalMs, 50, 250)
                        : Math.Max(500, options.CurrentValue.IdleIntervalMs);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "RF history sample skipped.");
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

        try { historyStore.Flush(); } catch { }
        try { analysisStore.Flush(); } catch { }
    }
}
