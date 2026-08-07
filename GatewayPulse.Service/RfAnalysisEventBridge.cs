using GatewayPulse.Core;
using GatewayPulse.RfMonitoring;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Emits RF Analysis markers for gateway restart and Winlink station connect/disconnect.
/// </summary>
public sealed class RfAnalysisEventBridge(
    GatewayPulseService pulse,
    RfAnalysisStore analysisStore,
    ILogger<RfAnalysisEventBridge> logger) : BackgroundService
{
    private string? _lastStation;
    private bool _restartLogged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_restartLogged)
        {
            _restartLogged = true;
            analysisStore.AddEvent(new RfAnalysisEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Type = RfAnalysisEventTypes.GatewayRestart,
                Detail = "Gateway Pulse service started"
            });
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Peek only — never call GetStatus() here (full log parse would fight the dashboard).
                var station = pulse.PeekLastStation()?.Trim();
                if (string.IsNullOrWhiteSpace(station))
                    station = null;

                if (!string.Equals(station, _lastStation, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(station))
                    {
                        analysisStore.AddEvent(new RfAnalysisEvent
                        {
                            Timestamp = DateTimeOffset.UtcNow,
                            Type = RfAnalysisEventTypes.WinlinkSessionStart,
                            Detail = $"Winlink station: {station}"
                        });
                    }
                    else if (!string.IsNullOrWhiteSpace(_lastStation))
                    {
                        analysisStore.AddEvent(new RfAnalysisEvent
                        {
                            Timestamp = DateTimeOffset.UtcNow,
                            Type = RfAnalysisEventTypes.WinlinkSessionEnd,
                            Detail = $"Winlink station ended: {_lastStation}"
                        });
                    }

                    _lastStation = station;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "RF analysis event bridge skipped.");
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
    }
}
