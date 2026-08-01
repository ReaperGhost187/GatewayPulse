using GatewayPulse.Core;
using GatewayPulse.RfMonitoring;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Optional LP-100A subsystem alerts. Independent from gateway/power alerts.
/// Alerts only on state changes while transmitting (except disconnect/stale/recovery).
/// </summary>
public sealed class RfAlertMonitor(
    IRfMonitor rfMonitor,
    PushoverService pushover,
    IOptionsMonitor<Lp100MonitorOptions> options,
    IOptionsMonitor<PushoverOptions> pushoverOptions,
    ILogger<RfAlertMonitor> logger) : BackgroundService
{
    private string? _activeCondition;
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
        if (!lp.Enabled || !alerts.Enabled || !pushoverOptions.CurrentValue.Enabled)
            return;

        var telemetry = await rfMonitor.GetTelemetryAsync();
        var cooldown = TimeSpan.FromMinutes(Math.Max(1, alerts.CooldownMinutes));
        string? condition = null;
        string? title = null;
        string? message = null;

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
                await MaybeSendAsync("recovery", "LP-100A Recovered", "RF meter connection restored.", cooldown, force: true);
            }
            _wasConnected = true;

            if (telemetry.Transmitting)
            {
                if (alerts.CriticalSwr && telemetry.Swr is decimal swrCrit && swrCrit >= alerts.SwrCritical)
                {
                    condition = "swr-critical";
                    title = "Critical SWR";
                    message = $"SWR {swrCrit:0.00} while transmitting (threshold {alerts.SwrCritical:0.00}).";
                }
                else if (alerts.HighSwr && telemetry.Swr is decimal swrWarn && swrWarn >= alerts.SwrWarning)
                {
                    condition = "swr-warning";
                    title = "High SWR";
                    message = $"SWR {swrWarn:0.00} while transmitting (threshold {alerts.SwrWarning:0.00}).";
                }
                else if (alerts.HighReflected &&
                         telemetry.ReflectedPowerWatts is decimal reflected &&
                         reflected >= alerts.ReflectedWarningWatts)
                {
                    condition = "reflected";
                    title = "High Reflected Power";
                    message = $"Reflected {reflected:0.#} W (threshold {alerts.ReflectedWarningWatts:0.#} W).";
                }
                else if (alerts.HighPowerWarningWatts is decimal highPower &&
                         telemetry.ForwardPowerWatts is decimal forward &&
                         forward >= highPower)
                {
                    condition = "high-power";
                    title = "High Forward Power";
                    message = $"Forward {forward:0.#} W (threshold {highPower:0.#} W).";
                }
            }
        }

        if (condition is null)
        {
            if (_activeCondition is not null && alerts.Recovery &&
                _activeCondition is not ("disconnected" or "stale" or "recovery"))
            {
                await MaybeSendAsync("cleared", "RF Alert Cleared", $"Condition '{_activeCondition}' cleared.", cooldown, force: true);
            }
            _activeCondition = null;
            return;
        }

        if (condition != _activeCondition || DateTimeOffset.UtcNow - _lastSent >= cooldown)
        {
            await MaybeSendAsync(condition, title!, message!, cooldown, force: condition != _activeCondition);
            _activeCondition = condition;
        }
    }

    private async Task MaybeSendAsync(string condition, string title, string message, TimeSpan cooldown, bool force)
    {
        if (!force && DateTimeOffset.UtcNow - _lastSent < cooldown && condition == _activeCondition)
            return;
        var ok = await pushover.SendAsync($"Gateway Pulse · {title}", message);
        if (ok)
        {
            _lastSent = DateTimeOffset.UtcNow;
            logger.LogInformation("RF alert sent: {Condition}", condition);
        }
    }
}
