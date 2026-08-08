using GatewayPulse.RfMonitoring;

namespace GatewayPulse.Lp100Monitor.CommandLine;

public enum MonitorMode
{
    Help,
    Device,
    Mock,
    Test
}

public sealed class MonitorOptions
{
    public MonitorMode Mode { get; init; } = MonitorMode.Help;
    public string? Port { get; init; }
    public bool AutoDetect { get; init; } = true;
    public int BaudRate { get; init; } = 115200;
    public string OutputPath { get; init; } = "RfTelemetry.json";
    public string LogsPath { get; init; } = "logs";
    public int IntervalMs { get; init; } = 80;
    public int IdleIntervalMs { get; init; } = 1000;
    public bool Once { get; init; }
    public bool ForceDemo { get; init; }
    public bool CaptureRaw { get; init; }
    public string? ConfigPath { get; init; }

    public static MonitorOptions Parse(string[] args)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
                continue;
            var value = (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                ? args[++i]
                : "true";
            map[key] = value;
        }

        if (map.ContainsKey("--help") || args.Length == 0)
            return new MonitorOptions { Mode = MonitorMode.Help };

        var mode = MonitorMode.Device;
        if (map.ContainsKey("--mock")) mode = MonitorMode.Mock;
        else if (map.ContainsKey("--test")) mode = MonitorMode.Test;

        var baud = 115200;
        if (map.TryGetValue("--baud", out var baudText) && int.TryParse(baudText, out var parsedBaud))
            baud = parsedBaud;

        var interval = 80;
        if (map.TryGetValue("--interval-ms", out var intervalText) && int.TryParse(intervalText, out var parsedInterval))
            interval = Math.Clamp(parsedInterval, 50, 5000);

        var idle = 1000;
        if (map.TryGetValue("--idle-interval-ms", out var idleText) && int.TryParse(idleText, out var parsedIdle))
            idle = Math.Clamp(parsedIdle, 250, 10000);

        var autoDetect = !map.ContainsKey("--no-auto-detect");
        if (map.TryGetValue("--auto-detect", out var autoText))
            autoDetect = !string.Equals(autoText, "false", StringComparison.OrdinalIgnoreCase);

        return new MonitorOptions
        {
            Mode = mode,
            Port = map.TryGetValue("--port", out var port)
                ? NullIfEmpty(SerialPortName.Normalize(port))
                : null,
            AutoDetect = autoDetect,
            BaudRate = baud,
            OutputPath = map.TryGetValue("--output", out var output) && !string.IsNullOrWhiteSpace(output)
                ? output!
                : "RfTelemetry.json",
            LogsPath = map.TryGetValue("--logs", out var logs) && !string.IsNullOrWhiteSpace(logs)
                ? logs!
                : "logs",
            IntervalMs = interval,
            IdleIntervalMs = idle,
            Once = map.ContainsKey("--once"),
            ForceDemo = map.ContainsKey("--force-demo"),
            CaptureRaw = map.ContainsKey("--capture"),
            ConfigPath = map.TryGetValue("--config", out var config) ? config : null
        };
    }

    public static string HelpText =>
        """
        GatewayPulse.Lp100Monitor — TelePost LP-100A read-only collector

        Serial (firmware ≥ 1.2.0.0): 115200 8N1, poll ASCII 'P', response ';Power,Z,Phase,...'
        Only the documented poll command is sent. A/M/F are never used.
        Values are display snapshots (not RF-envelope samples). Prefer Peak Hold for PACTOR.

        --port COMx              Preferred COM port
        --auto-detect            Try available ports (default on)
        --no-auto-detect         Use --port only
        --baud 115200            Baud rate (115200 default; older firmware may need 38400/19200)
        --output path            Atomic JSON telemetry path
        --logs path              Log directory
        --interval-ms 80         Poll interval while transmitting (min 50; ~50–80 for PACTOR)
        --idle-interval-ms 1000  Poll interval while idle (min 250)
        --capture                Record timestamped raw P responses under --logs (bounded)
        --once                   Single sample then exit
        --test                   Open port, poll once, write result, exit
        --mock                   Simulated telemetry (dev only)
        --force-demo             Allow mock writes under C:\PWM
        --help                   Show help
        """;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
