using GatewayPulse.Core;
using GatewayPulse.RfMonitoring;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Optional LP-100A subsystem alerts. Independent from gateway/power alerts.
/// Alerts only on state changes while transmitting (except disconnect/stale/recovery).
/// Pushover path unchanged; mobile events are published in parallel after send decisions.
/// </summary>
public sealed class RfAlertMonitor(
    IRfMonitor rfMonitor,
    PushoverService pushover,
    IMobileAlertPublisher mobileAlerts,
    MobilePushDeliveryTracker deliveryTracker,
    IOptionsMonitor<Lp100MonitorOptions> options,
    IOptionsMonitor<PushoverOptions> pushoverOptions,
    IOptionsMonitor<ApplePushOptions> applePushOptions,
    IOptionsMonitor<GatewayPulseOptions> gatewayOptions,
    ILogger<RfAlertMonitor> logger) : BackgroundService
{
    private string? _activeCondition;
    // RF mobile marker semantics (Fix C):
    // - queued: event accepted by the bounded dispatch queue (_lastMobileQueuedCondition).
    //   Prevents re-enqueue every poll while the same RF condition stays active.
    // - delivered: at least one APNs device succeeded (_lastMobileDeliveredCondition),
    //   observed via MobilePushDeliveryTracker without awaiting Apple I/O.
    // - all-fail retry: if queued but tracker reports Failed, allow re-enqueue only after
    //   the RF cooldown elapses since _lastMobileQueuedAt (not every poll / Pushover retry).
    // - On condition clear: clear markers, queue timestamp, and the tracker key.
    private string? _lastMobileQueuedCondition;
    private string? _lastMobileDeliveredCondition;
    private DateTimeOffset _lastMobileQueuedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSent = DateTimeOffset.MinValue;
    private bool _wasConnected = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "RF alert evaluation skipped.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task EvaluateAsync()
    {
        var lp = options.CurrentValue;
        var alerts = lp.Alerts ?? new Lp100AlertOptions();
        // Evaluate when LP-100 alerts are enabled and at least one notification channel is on.
        var pushoverEnabled = pushoverOptions.CurrentValue.Enabled;
        var appleEnabled = applePushOptions.CurrentValue.Enabled;
        if (!lp.Enabled || !alerts.Enabled || (!pushoverEnabled && !appleEnabled))
            return;

        var telemetry = await rfMonitor.GetTelemetryAsync();
        var cooldown = TimeSpan.FromMinutes(Math.Max(1, alerts.CooldownMinutes));
        string? condition = null;
        string? title = null;
        string? message = null;
        decimal? measured = null;
        decimal? threshold = null;
        string? unit = null;

        if (!telemetry.Connected)
        {
            if (alerts.Disconnected && _wasConnected)
            {
                condition = "disconnected";
                title = "LP-100A Disconnected";
                message = telemetry.Error ?? "RF meter is not connected.";
            }
            else if (alerts.Stale && telemetry.Stale)
            {
                condition = "stale";
                title = "LP-100A Telemetry Stale";
                message = telemetry.Error ?? "RF telemetry is stale.";
            }
            _wasConnected = telemetry.Connected;
        }
        else
        {
            if (!_wasConnected && alerts.Recovery)
            {
                await MaybeSendAsync(
                    "recovery",
                    "LP-100A Recovered",
                    "RF meter connection restored.",
                    cooldown,
                    force: true,
                    measured: null,
                    threshold: null,
                    unit: null);
            }
            _wasConnected = true;

            if (telemetry.Transmitting)
            {
                if (alerts.CriticalSwr && telemetry.Swr is decimal swrCrit && swrCrit >= alerts.SwrCritical)
                {
                    condition = "swr-critical";
                    title = "Critical SWR";
                    message = $"SWR {swrCrit:0.00} while transmitting (threshold {alerts.SwrCritical:0.00}).";
                    measured = swrCrit;
                    threshold = alerts.SwrCritical;
                    unit = "SWR";
                }
                else if (alerts.HighSwr && telemetry.Swr is decimal swrWarn && swrWarn >= alerts.SwrWarning)
                {
                    condition = "swr-warning";
                    title = "High SWR";
                    message = $"SWR {swrWarn:0.00} while transmitting (threshold {alerts.SwrWarning:0.00}).";
                    measured = swrWarn;
                    threshold = alerts.SwrWarning;
                    unit = "SWR";
                }
                else if (alerts.HighReflected &&
                         telemetry.ReflectedPowerWatts is decimal reflected &&
                         reflected >= alerts.ReflectedWarningWatts)
                {
                    condition = "reflected";
                    title = "High Reflected Power";
                    message = $"Reflected {reflected:0.#} W (threshold {alerts.ReflectedWarningWatts:0.#} W).";
                    measured = reflected;
                    threshold = alerts.ReflectedWarningWatts;
                    unit = "W";
                }
                else if (alerts.HighPowerWarningWatts is decimal highPower &&
                         telemetry.ForwardPowerWatts is decimal forward &&
                         forward >= highPower)
                {
                    condition = "high-power";
                    title = "High Forward Power";
                    message = $"Forward {forward:0.#} W (threshold {highPower:0.#} W).";
                    measured = forward;
                    threshold = highPower;
                    unit = "W";
                }
            }
        }

        if (condition is null)
        {
            if (_activeCondition is not null && alerts.Recovery &&
                _activeCondition is not ("disconnected" or "stale" or "recovery"))
            {
                await MaybeSendAsync(
                    "cleared",
                    "RF Alert Cleared",
                    $"Condition '{_activeCondition}' cleared.",
                    cooldown,
                    force: true,
                    measured: null,
                    threshold: null,
                    unit: null);
            }
            if (_activeCondition is not null)
                deliveryTracker.Clear(RfDedupeKey(_activeCondition));
            _activeCondition = null;
            _lastMobileQueuedCondition = null;
            _lastMobileDeliveredCondition = null;
            _lastMobileQueuedAt = DateTimeOffset.MinValue;
            return;
        }

        if (condition != _activeCondition || DateTimeOffset.UtcNow - _lastSent >= cooldown)
        {
            await MaybeSendAsync(
                condition,
                title!,
                message!,
                cooldown,
                force: condition != _activeCondition,
                measured,
                threshold,
                unit);
            _activeCondition = condition;
        }
    }

    private async Task MaybeSendAsync(
        string condition,
        string title,
        string message,
        TimeSpan cooldown,
        bool force,
        decimal? measured,
        decimal? threshold,
        string? unit)
    {
        if (!force && DateTimeOffset.UtcNow - _lastSent < cooldown && condition == _activeCondition)
            return;

        var pushoverEnabled = pushoverOptions.CurrentValue.Enabled;
        var appleEnabled = applePushOptions.CurrentValue.Enabled;
        var pushoverOk = false;

        if (pushoverEnabled)
        {
            pushoverOk = await pushover.SendAsync($"Gateway Pulse · {title}", message);
            if (pushoverOk)
                logger.LogInformation("RF alert sent: {Condition}", condition);
        }

        // Enqueue RF condition to APNs via bounded dispatcher (never awaits Apple network I/O).
        if (appleEnabled)
            await MaybeEnqueueMobileAsync(condition, title, message, measured, threshold, unit, cooldown);

        // Pushover keeps legacy retry-until-success cooldown; APNs-only advances immediately.
        if (pushoverOk || (!pushoverEnabled && appleEnabled))
            _lastSent = DateTimeOffset.UtcNow;
    }

    private async Task MaybeEnqueueMobileAsync(
        string condition,
        string title,
        string message,
        decimal? measured,
        decimal? threshold,
        string? unit,
        TimeSpan cooldown)
    {
        var dedupeKey = RfDedupeKey(condition);

        // Promote tracker delivery → delivered marker (no duplicate notifications).
        if (deliveryTracker.WasDelivered(dedupeKey))
            _lastMobileDeliveredCondition = condition;

        if (string.Equals(_lastMobileDeliveredCondition, condition, StringComparison.Ordinal))
            return;

        var queuedSame = string.Equals(_lastMobileQueuedCondition, condition, StringComparison.Ordinal);
        if (queuedSame)
        {
            if (deliveryTracker.IsPending(dedupeKey))
                return; // still in flight — do not spam the queue

            if (!deliveryTracker.HasFailed(dedupeKey))
                return;

            // All-fail retry uses its own cooldown clock so Pushover's retry-every-poll
            // path cannot re-enqueue mobile alerts every 2 seconds.
            if (DateTimeOffset.UtcNow - _lastMobileQueuedAt < cooldown)
                return;
        }

        var mobileEvent = MobileAlertEventFactory.FromRfCondition(
            condition,
            title,
            message,
            measured,
            threshold,
            unit,
            frequencyKhz: null,
            gatewayOptions.CurrentValue.GatewayName);
        if (mobileEvent is null)
            return;

        mobileEvent.DedupeKey = dedupeKey;
        var ack = await mobileAlerts.PublishAsync(mobileEvent);
        if (ack.Accepted)
        {
            _lastMobileQueuedCondition = condition;
            _lastMobileQueuedAt = DateTimeOffset.UtcNow;
        }
    }

    private static string RfDedupeKey(string condition) => "rf:" + condition;
}
