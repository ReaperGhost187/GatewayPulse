using GatewayPulse.RfMonitoring;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

public sealed class RfHistoryCollector(
    IRfMonitor rfMonitor,
    RfHistoryStore historyStore,
    IOptionsMonitor<Lp100MonitorOptions> options,
    ILogger<RfHistoryCollector> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.CurrentValue.HistoryEnabled)
                {
                    var telemetry = await rfMonitor.GetTelemetryAsync();
                    historyStore.Record(telemetry);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "RF history sample skipped.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        try { historyStore.Flush(); } catch { }
    }
}
