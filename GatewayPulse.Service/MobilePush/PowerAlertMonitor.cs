using GatewayPulse.Core;
using GatewayPulse.PowerMonitoring;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Watches composed power telemetry transitions and emits mobile alert events.
/// Uses the same status bands as PowerSystemComposer; does not change thresholds
/// or Victron collector behavior. No Pushover side effects.
/// </summary>
public sealed class PowerAlertMonitor(
    IPowerMonitor powerMonitor,
    IMobileAlertPublisher mobileAlerts,
    IOptionsMonitor<ApplePushOptions> applePush,
    IOptionsMonitor<GatewayPulseOptions> gatewayOptions,
    ILogger<PowerAlertMonitor> logger) : BackgroundService
{
    private bool _primed;
    private string? _lastSystemStatus;
    private bool? _lastAlarm;
    private bool? _lastOutputEnabled;
    private readonly Dictionary<string, DeviceSnapshot> _devices = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (applePush.CurrentValue.Enabled)
                    await EvaluateAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Power mobile alert evaluation skipped.");
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

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var telemetry = await powerMonitor.GetTelemetryAsync();
        var gatewayName = gatewayOptions.CurrentValue.GatewayName;
        var status = telemetry.System?.Status ?? "Unavailable";
        var alarm = telemetry.System?.Alarm == true || telemetry.Alarm == true;
        var outputEnabled = telemetry.System?.OutputEnabled ?? telemetry.OutputEnabled;

        if (!_primed)
        {
            Prime(telemetry, status, alarm, outputEnabled);
            return;
        }

        // System status transitions (Healthy/Warning/Critical/Unavailable).
        if (!string.Equals(status, _lastSystemStatus, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(status, "Critical", StringComparison.OrdinalIgnoreCase))
            {
                await PublishAsync(new MobileAlertEvent
                {
                    Type = MobileAlertTypes.PowerCritical,
                    Source = "power",
                    Severity = MobileAlertSeverity.Critical,
                    Title = "Power Critical",
                    Message = BuildSystemMessage(telemetry, "Power system is critical."),
                    MeasuredValue = telemetry.System?.StateOfCharge ?? telemetry.StateOfCharge,
                    Unit = telemetry.System?.StateOfCharge is not null || telemetry.StateOfCharge is not null ? "%" : null,
                    GatewayName = gatewayName
                }, cancellationToken);
            }
            else if (string.Equals(status, "Warning", StringComparison.OrdinalIgnoreCase))
            {
                await PublishAsync(new MobileAlertEvent
                {
                    Type = MobileAlertTypes.PowerWarning,
                    Source = "power",
                    Severity = MobileAlertSeverity.Warning,
                    Title = "Power Warning",
                    Message = BuildSystemMessage(telemetry, "Power system warning."),
                    MeasuredValue = telemetry.System?.StateOfCharge ?? telemetry.StateOfCharge,
                    Unit = telemetry.System?.StateOfCharge is not null || telemetry.StateOfCharge is not null ? "%" : null,
                    GatewayName = gatewayName
                }, cancellationToken);
            }
            else if (string.Equals(status, "Healthy", StringComparison.OrdinalIgnoreCase) &&
                     _lastSystemStatus is "Warning" or "Critical")
            {
                await PublishAsync(new MobileAlertEvent
                {
                    Type = MobileAlertTypes.PowerRecovery,
                    Source = "power",
                    Severity = MobileAlertSeverity.Info,
                    Title = "Power Recovered",
                    Message = "Power system status is healthy again.",
                    IsRecovery = true,
                    GatewayName = gatewayName
                }, cancellationToken);
            }

            _lastSystemStatus = status;
        }

        if (_lastAlarm != true && alarm)
        {
            await PublishAsync(new MobileAlertEvent
            {
                Type = MobileAlertTypes.PowerAlarm,
                Source = "power",
                Severity = MobileAlertSeverity.Critical,
                Title = "Power Alarm",
                Message = string.IsNullOrWhiteSpace(telemetry.System?.AlarmReason ?? telemetry.AlarmReason)
                    ? "A power device alarm was raised."
                    : (telemetry.System?.AlarmReason ?? telemetry.AlarmReason)!,
                GatewayName = gatewayName
            }, cancellationToken);
        }
        else if (_lastAlarm == true && !alarm)
        {
            await PublishAsync(new MobileAlertEvent
            {
                Type = MobileAlertTypes.PowerRecovery,
                Source = "power",
                Severity = MobileAlertSeverity.Info,
                Title = "Power Alarm Cleared",
                Message = "Power device alarm cleared.",
                IsRecovery = true,
                GatewayName = gatewayName
            }, cancellationToken);
        }
        _lastAlarm = alarm;

        // BatteryProtect output — only when the field is present (not N/A).
        if (outputEnabled.HasValue)
        {
            if (_lastOutputEnabled == true && outputEnabled == false)
            {
                await PublishAsync(new MobileAlertEvent
                {
                    Type = MobileAlertTypes.PowerOutputOff,
                    Source = "power",
                    Severity = MobileAlertSeverity.Critical,
                    Title = "BatteryProtect Output Off",
                    Message = "BatteryProtect output is disabled.",
                    GatewayName = gatewayName
                }, cancellationToken);
            }
            else if (_lastOutputEnabled == false && outputEnabled == true)
            {
                await PublishAsync(new MobileAlertEvent
                {
                    Type = MobileAlertTypes.PowerRecovery,
                    Source = "power",
                    Severity = MobileAlertSeverity.Info,
                    Title = "BatteryProtect Output Restored",
                    Message = "BatteryProtect output restored.",
                    IsRecovery = true,
                    GatewayName = gatewayName
                }, cancellationToken);
            }
            _lastOutputEnabled = outputEnabled;
        }

        foreach (var device in telemetry.Devices ?? [])
        {
            var key = !string.IsNullOrWhiteSpace(device.DeviceId)
                ? device.DeviceId!
                : device.Type;
            _devices.TryGetValue(key, out var previous);
            var current = new DeviceSnapshot(device.Connected, device.Stale, device.Type);

            if (previous is not null)
            {
                if (previous.Connected && !current.Connected)
                {
                    await PublishAsync(new MobileAlertEvent
                    {
                        Type = MobileAlertTypes.PowerDeviceDisconnected,
                        Source = "power",
                        Severity = MobileAlertSeverity.Warning,
                        Title = $"{device.Type} Disconnected",
                        Message = $"{device.Device} disconnected.",
                        GatewayName = gatewayName
                    }, cancellationToken);
                }
                else if (!previous.Connected && current.Connected)
                {
                    await PublishAsync(new MobileAlertEvent
                    {
                        Type = MobileAlertTypes.PowerRecovery,
                        Source = "power",
                        Severity = MobileAlertSeverity.Info,
                        Title = $"{device.Type} Reconnected",
                        Message = $"{device.Device} reconnected.",
                        IsRecovery = true,
                        GatewayName = gatewayName
                    }, cancellationToken);
                }

                if (!previous.Stale && current.Stale)
                {
                    await PublishAsync(new MobileAlertEvent
                    {
                        Type = MobileAlertTypes.PowerDeviceStale,
                        Source = "power",
                        Severity = MobileAlertSeverity.Warning,
                        Title = $"{device.Type} Stale",
                        Message = $"{device.Device} telemetry is stale.",
                        GatewayName = gatewayName
                    }, cancellationToken);
                }
                else if (previous.Stale && !current.Stale && current.Connected)
                {
                    await PublishAsync(new MobileAlertEvent
                    {
                        Type = MobileAlertTypes.PowerRecovery,
                        Source = "power",
                        Severity = MobileAlertSeverity.Info,
                        Title = $"{device.Type} Telemetry Recovered",
                        Message = $"{device.Device} telemetry recovered.",
                        IsRecovery = true,
                        GatewayName = gatewayName
                    }, cancellationToken);
                }
            }

            _devices[key] = current;
        }
    }

    private void Prime(PowerTelemetry telemetry, string status, bool alarm, bool? outputEnabled)
    {
        _lastSystemStatus = status;
        _lastAlarm = alarm;
        _lastOutputEnabled = outputEnabled;
        _devices.Clear();
        foreach (var device in telemetry.Devices ?? [])
        {
            var key = !string.IsNullOrWhiteSpace(device.DeviceId) ? device.DeviceId! : device.Type;
            _devices[key] = new DeviceSnapshot(device.Connected, device.Stale, device.Type);
        }
        _primed = true;
    }

    private Task PublishAsync(MobileAlertEvent alertEvent, CancellationToken cancellationToken) =>
        mobileAlerts.PublishAsync(alertEvent, cancellationToken);

    private static string BuildSystemMessage(PowerTelemetry telemetry, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(telemetry.System?.AlarmReason))
            return telemetry.System!.AlarmReason!;
        if (!string.IsNullOrWhiteSpace(telemetry.Error))
            return telemetry.Error!;
        if (telemetry.System?.StateOfCharge is decimal soc)
            return $"{fallback} State of charge {soc:0.#}%.";
        return fallback;
    }

    private sealed record DeviceSnapshot(bool Connected, bool Stale, string Type);
}
