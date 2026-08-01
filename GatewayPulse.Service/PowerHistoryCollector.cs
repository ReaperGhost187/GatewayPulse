using GatewayPulse.PowerMonitoring;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Periodically samples live power telemetry into the rolling history store.
/// Runs off the request path so dashboard refreshes stay light.
/// </summary>
public sealed class PowerHistoryCollector : BackgroundService
{
    private readonly IPowerMonitor _powerMonitor;
    private readonly PowerHistoryStore _historyStore;
    private readonly ILogger<PowerHistoryCollector> _logger;
    private readonly TimeSpan _interval;

    public PowerHistoryCollector(
        IPowerMonitor powerMonitor,
        PowerHistoryStore historyStore,
        ILogger<PowerHistoryCollector> logger,
        TimeSpan? interval = null)
    {
        _powerMonitor = powerMonitor;
        _historyStore = historyStore;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromSeconds(PowerHistoryStore.DefaultMinSampleSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Touch the store once so historical samples load before the first chart request.
        _ = _historyStore.Count;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var telemetry = await _powerMonitor.GetTelemetryAsync();
                _historyStore.Record(telemetry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Power history sample skipped.");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
