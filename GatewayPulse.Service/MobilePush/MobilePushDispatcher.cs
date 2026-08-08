using GatewayPulse.Core;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Hosted APNs delivery worker. Drains the bounded dispatch queue independently of
/// RF / Power / gateway monitoring loops. Stops cleanly on shutdown.
/// </summary>
public sealed class MobilePushDispatcher(
    MobilePushDispatchQueue queue,
    MobileAlertRouter router,
    MobilePushDeliveryTracker tracker,
    ILogger<MobilePushDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                MobileAlertEvent? alertEvent;
                try
                {
                    alertEvent = await queue.DequeueAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                if (alertEvent is null)
                    break;

                await DeliverAsync(alertEvent, stoppingToken);
            }
        }
        finally
        {
            queue.Complete();
        }
    }

    private async Task DeliverAsync(MobileAlertEvent alertEvent, CancellationToken cancellationToken)
    {
        try
        {
            var result = await router.RouteAsync(alertEvent, cancellationToken);
            if (!string.IsNullOrWhiteSpace(alertEvent.DedupeKey))
            {
                // delivered = at least one device succeeded; otherwise failed (allows RF cooldown retry).
                tracker.Report(alertEvent.DedupeKey, result.SuccessCount > 0);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!string.IsNullOrWhiteSpace(alertEvent.DedupeKey))
                tracker.Report(alertEvent.DedupeKey, anySuccess: false);
            throw;
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(alertEvent.DedupeKey))
                tracker.Report(alertEvent.DedupeKey, anySuccess: false);

            logger.LogWarning(
                "Mobile push dispatch failed alertType={AlertType} error={Error}",
                alertEvent.Type,
                ApnsLogSanitizer.Describe(ex));
        }
    }
}
