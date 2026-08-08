using GatewayPulse.Core;

namespace GatewayPulse.ServiceHosting;

/// <summary>Maps existing alert decision contexts onto canonical MobileAlertEvent types.</summary>
public static class MobileAlertEventFactory
{
    public static IEnumerable<MobileAlertEvent> FromGatewayProblems(
        IEnumerable<string> problems,
        string? gatewayName,
        IEnumerable<string>? previousProblems = null)
    {
        var previous = new HashSet<string>(previousProblems ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var problem in problems)
        {
            if (previous.Contains(problem))
                continue;

            var mapped = MapGatewayProblem(problem, gatewayName);
            if (mapped is not null)
                yield return mapped;
        }
    }

    public static MobileAlertEvent GatewayRecovery(string? gatewayName) => new()
    {
        Type = MobileAlertTypes.GatewayRecovery,
        Source = "gateway",
        Severity = MobileAlertSeverity.Info,
        Title = "Gateway Pulse Recovery",
        Message = "Gateway health is restored.",
        IsRecovery = true,
        GatewayName = gatewayName
    };

    public static MobileAlertEvent StationConnected(string? gatewayName, string station, string? relayEvent) => new()
    {
        Type = MobileAlertTypes.GatewayStationConnected,
        Source = "gateway",
        Severity = MobileAlertSeverity.Info,
        Title = "Station Connected",
        Message = string.IsNullOrWhiteSpace(relayEvent)
            ? $"Station connected: {station}"
            : $"Station connected: {station}\n{relayEvent}",
        GatewayName = gatewayName
    };

    public static MobileAlertEvent? FromRfCondition(
        string condition,
        string title,
        string message,
        decimal? measuredValue = null,
        decimal? threshold = null,
        string? unit = null,
        decimal? frequencyKhz = null,
        string? gatewayName = null)
    {
        var type = condition switch
        {
            "disconnected" => MobileAlertTypes.RfDisconnected,
            "stale" => MobileAlertTypes.RfStale,
            "recovery" => MobileAlertTypes.RfRecovery,
            "swr-critical" => MobileAlertTypes.RfSwrCritical,
            "swr-warning" => MobileAlertTypes.RfSwrWarning,
            "reflected" => MobileAlertTypes.RfReflected,
            "high-power" => MobileAlertTypes.RfHighPower,
            "cleared" => MobileAlertTypes.RfCleared,
            _ => null
        };
        if (type is null)
            return null;

        var severity = type switch
        {
            MobileAlertTypes.RfSwrCritical => MobileAlertSeverity.Critical,
            MobileAlertTypes.RfSwrWarning or MobileAlertTypes.RfReflected or MobileAlertTypes.RfHighPower
                or MobileAlertTypes.RfDisconnected or MobileAlertTypes.RfStale => MobileAlertSeverity.Warning,
            _ => MobileAlertSeverity.Info
        };

        return new MobileAlertEvent
        {
            Type = type,
            Source = "rf",
            Severity = severity,
            Title = title,
            Message = message,
            IsRecovery = type is MobileAlertTypes.RfRecovery or MobileAlertTypes.RfCleared,
            MeasuredValue = measuredValue,
            Threshold = threshold,
            Unit = unit,
            FrequencyKhz = frequencyKhz,
            GatewayName = gatewayName
        };
    }

    private static MobileAlertEvent? MapGatewayProblem(string problem, string? gatewayName)
    {
        if (problem.Contains("Relay", StringComparison.OrdinalIgnoreCase))
        {
            return new MobileAlertEvent
            {
                Type = MobileAlertTypes.GatewayRelayOffline,
                Source = "gateway",
                Severity = MobileAlertSeverity.Warning,
                Title = "RMS Relay Offline",
                Message = problem,
                GatewayName = gatewayName
            };
        }

        if (problem.Contains("Trimode", StringComparison.OrdinalIgnoreCase))
        {
            return new MobileAlertEvent
            {
                Type = MobileAlertTypes.GatewayTrimodeOffline,
                Source = "gateway",
                Severity = MobileAlertSeverity.Warning,
                Title = "RMS Trimode Offline",
                Message = problem,
                GatewayName = gatewayName
            };
        }

        if (problem.Contains("Scanner", StringComparison.OrdinalIgnoreCase))
        {
            return new MobileAlertEvent
            {
                Type = MobileAlertTypes.GatewayScannerStopped,
                Source = "gateway",
                Severity = MobileAlertSeverity.Warning,
                Title = "Scanner Stopped",
                Message = problem,
                GatewayName = gatewayName
            };
        }

        return null;
    }
}
