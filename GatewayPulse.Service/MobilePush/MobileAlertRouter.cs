using GatewayPulse.Core;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Loads registered devices, filters by preferences, sends via APNs.
/// Fail-soft; Pushover is never touched here.
/// </summary>
public sealed class MobileAlertRouter
{
    private readonly MobileDeviceRegistry _registry;
    private readonly IApnsClient _apnsClient;
    private readonly MobilePushHistory _history;
    private readonly IOptionsMonitor<ApplePushOptions> _options;
    private readonly ILogger<MobileAlertRouter> _logger;

    public MobileAlertRouter(
        MobileDeviceRegistry registry,
        IApnsClient apnsClient,
        MobilePushHistory history,
        IOptionsMonitor<ApplePushOptions> options,
        ILogger<MobileAlertRouter> logger)
    {
        _registry = registry;
        _apnsClient = apnsClient;
        _history = history;
        _options = options;
        _logger = logger;
    }

    public async Task<MobilePushRouteResult> RouteAsync(
        MobileAlertEvent alertEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);

        var result = new MobilePushRouteResult { AlertId = alertEvent.Id, AlertType = alertEvent.Type };
        if (!_options.CurrentValue.Enabled)
        {
            result.Outcome = "disabled";
            return result;
        }

        IReadOnlyList<MobileDeviceRecord> devices;
        try
        {
            devices = _registry.GetAll();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Mobile device registry read failed error={Error}",
                ApnsLogSanitizer.Describe(ex));
            result.Outcome = "registry_error";
            return result;
        }

        var subscribers = devices
            .Where(d => !d.TokenStale && !string.IsNullOrWhiteSpace(d.ApnsToken))
            .Where(d => MobilePushPreferences.Normalize(d.Preferences).Allows(alertEvent.Type))
            .ToList();

        result.CandidateCount = subscribers.Count;
        if (subscribers.Count == 0)
        {
            result.Outcome = "no_subscribers";
            return result;
        }

        foreach (var device in subscribers)
        {
            try
            {
                var send = await _apnsClient.SendAsync(device, alertEvent, cancellationToken);
                result.Attempted++;
                if (send.Success)
                {
                    result.SuccessCount++;
                    result.DeliveredDeviceIds.Add(device.DeviceId);
                }
                else
                {
                    result.FailureCount++;
                    if (send.TokenInvalid)
                    {
                        try { _registry.MarkTokenStale(device.DeviceId); }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(
                                "Failed to mark token stale for deviceId={DeviceId} error={Error}",
                                device.DeviceId,
                                ApnsLogSanitizer.Describe(ex));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Attempted++;
                result.FailureCount++;
                // Never LogWarning(ex, ...): exception text may embed APNs RequestUri/token.
                _logger.LogWarning(
                    "APNs route failed deviceId={DeviceId} alertType={AlertType} environment={Environment} category={Category} error={Error}",
                    device.DeviceId,
                    alertEvent.Type,
                    ApnsEnvironments.Normalize(device.Environment),
                    ApnsLogSanitizer.Category(ex),
                    ApnsLogSanitizer.Describe(ex));
            }
        }

        try
        {
            _history.Record(new MobilePushHistoryEntry
            {
                Id = alertEvent.Id,
                Type = alertEvent.Type,
                Severity = alertEvent.Severity,
                Title = alertEvent.Title,
                OccurredAt = alertEvent.OccurredAt,
                SentAt = DateTimeOffset.UtcNow,
                DeviceCount = subscribers.Count,
                SuccessCount = result.SuccessCount,
                FailureCount = result.FailureCount,
                DeviceIds = result.DeliveredDeviceIds.ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                "Mobile push history write skipped error={Error}",
                ApnsLogSanitizer.Describe(ex));
        }

        result.Outcome = result.SuccessCount > 0 ? "delivered" : "failed";
        return result;
    }
}

public sealed class MobilePushRouteResult
{
    public string AlertId { get; set; } = "";
    public string AlertType { get; set; } = "";
    public string Outcome { get; set; } = "";
    public int CandidateCount { get; set; }
    public int Attempted { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public List<string> DeliveredDeviceIds { get; } = [];
}

/// <summary>
/// Publishes by enqueuing onto the bounded APNs dispatcher. Completes without awaiting
/// Apple network I/O so RF/Power/gateway loops stay decoupled from push delivery.
/// </summary>
public sealed class MobileAlertPublisher : IMobileAlertPublisher
{
    private readonly MobilePushDispatchQueue _queue;
    private readonly IOptionsMonitor<ApplePushOptions> _options;
    private readonly ILogger<MobileAlertPublisher> _logger;

    public MobileAlertPublisher(
        MobilePushDispatchQueue queue,
        IOptionsMonitor<ApplePushOptions> options,
        ILogger<MobileAlertPublisher> logger)
    {
        _queue = queue;
        _options = options;
        _logger = logger;
    }

    public Task<MobilePublishAck> PublishAsync(MobileAlertEvent alertEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);

        if (!_options.CurrentValue.Enabled)
            return Task.FromResult(new MobilePublishAck { Accepted = false, Outcome = MobilePublishOutcomes.Disabled });

        try
        {
            var ack = _queue.Enqueue(alertEvent);
            return Task.FromResult(ack);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Mobile alert enqueue failed alertType={AlertType} error={Error}",
                alertEvent.Type,
                ApnsLogSanitizer.Describe(ex));
            return Task.FromResult(new MobilePublishAck { Accepted = false, Outcome = MobilePublishOutcomes.Dropped });
        }
    }
}
