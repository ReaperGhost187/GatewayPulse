using GatewayPulse.Core;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Keeps Trimode scanner/frequency observations fresh between heavy /api/status refreshes.
/// </summary>
public sealed class TrimodeLivePoller(
    GatewayPulseService pulse,
    IOptionsMonitor<DashboardOptions> dashboard,
    ILogger<TrimodeLivePoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                pulse.RefreshLiveRadioState();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Trimode live radio poll skipped.");
            }

            var seconds = Math.Clamp(dashboard.CurrentValue.LiveRadioSeconds, 1, 5);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
