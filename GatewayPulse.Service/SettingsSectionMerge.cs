using System.Text.Json;
using System.Text.Json.Nodes;
using GatewayPulse.Core;
using GatewayPulse.RfMonitoring;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Merges settings UI POSTs into appsettings.json without wiping sibling sections
/// when RadioCat or Lp100Monitor are omitted from the payload.
/// </summary>
public static class SettingsSectionMerge
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void ApplyRadioCat(JsonObject gatewayPulse, RadioCatOptions? incoming)
    {
        ArgumentNullException.ThrowIfNull(gatewayPulse);
        if (incoming is null)
            return;

        if (incoming.Port <= 0) incoming.Port = 4532;
        if (incoming.TimeoutMs < 100) incoming.TimeoutMs = 400;
        if (string.IsNullOrWhiteSpace(incoming.Host)) incoming.Host = "127.0.0.1";
        if (incoming.BaudRate <= 0) incoming.BaudRate = 19200;
        if (incoming.PollSeconds < 1) incoming.PollSeconds = 2;
        if (incoming.PollSeconds > 30) incoming.PollSeconds = 30;
        if (string.IsNullOrWhiteSpace(incoming.Mode)) incoming.Mode = "CivCom";
        if (string.IsNullOrWhiteSpace(incoming.CivAddress)) incoming.CivAddress = "94";
        incoming.PortName = SerialPortName.Normalize(incoming.PortName);
        incoming.CivAddress = incoming.CivAddress.Trim();
        incoming.Mode = incoming.Mode.Trim();

        gatewayPulse["RadioCat"] = JsonSerializer.SerializeToNode(incoming, Indented);
    }

    public static void ApplyLp100Monitor(JsonObject root, Lp100MonitorOptions? incoming)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (incoming is null)
            return;

        if (incoming.BaudRate <= 0) incoming.BaudRate = 115200;
        if (incoming.IntervalMs < 50) incoming.IntervalMs = 80;
        if (incoming.IdleIntervalMs < 250) incoming.IdleIntervalMs = 1000;
        if (incoming.RestartDelaySeconds < 1) incoming.RestartDelaySeconds = 10;
        if (incoming.TxThresholdWatts <= 0) incoming.TxThresholdWatts = 0.05m;
        if (incoming.SwrMinForwardWatts <= 0) incoming.SwrMinForwardWatts = 0.5m;
        if (incoming.SessionCoalesceMs < 100 && incoming.TxEndDebounceMs >= 100)
            incoming.SessionCoalesceMs = incoming.TxEndDebounceMs;
        if (incoming.SessionCoalesceMs < 100) incoming.SessionCoalesceMs = 6000;
        incoming.TxEndDebounceMs = incoming.SessionCoalesceMs;
        incoming.Port = SerialPortName.Normalize(incoming.Port);
        incoming.Alerts ??= new Lp100AlertOptions();

        root["Lp100Monitor"] = JsonSerializer.SerializeToNode(incoming, Indented);

        var rfMonitoring = root["RfMonitoring"] as JsonObject ?? new JsonObject();
        root["RfMonitoring"] = rfMonitoring;
        rfMonitoring["TelemetryPath"] = string.IsNullOrWhiteSpace(incoming.OutputPath)
            ? @"C:\PWM\RfTelemetry.json"
            : incoming.OutputPath;
        if (rfMonitoring["HistoryPath"] is null)
            rfMonitoring["HistoryPath"] = @"C:\PWM\RfHistory.json";
        if (rfMonitoring["AnalysisPath"] is null)
            rfMonitoring["AnalysisPath"] = @"C:\PWM\RfAnalysis.json";
        if (rfMonitoring["SwrByFrequencyPath"] is null)
            rfMonitoring["SwrByFrequencyPath"] = @"C:\PWM\RfSwrByFrequency.json";
        if (rfMonitoring["TransmissionHistoryPath"] is null)
            rfMonitoring["TransmissionHistoryPath"] = @"C:\PWM\RfTransmissionHistory.json";
        if (rfMonitoring["StaleAfterSeconds"] is null)
            rfMonitoring["StaleAfterSeconds"] = 10;
    }
}
