using System.Diagnostics;
using GatewayPulse.RfMonitoring;

namespace GatewayPulse.ServiceHosting;

public sealed class Lp100MonitorOptions
{
    public bool Enabled { get; set; }
    public string ExecutablePath { get; set; } = @"Lp100Monitor\GatewayPulse.Lp100Monitor.exe";
    public string Port { get; set; } = "";
    public bool AutoDetect { get; set; } = true;
    public int BaudRate { get; set; } = 115200;
    public string OutputPath { get; set; } = @"C:\PWM\RfTelemetry.json";
    public string LogsPath { get; set; } = @"C:\PWM\logs";
    /// <summary>TX poll interval. ~50–80 ms for PACTOR display snapshots (min 50).</summary>
    public int IntervalMs { get; set; } = 80;
    public int IdleIntervalMs { get; set; } = 1000;
    public int RestartDelaySeconds { get; set; } = 10;
    public bool HistoryEnabled { get; set; } = true;
    /// <summary>Forward power (W) above this starts / continues an RF transmission session.</summary>
    public decimal TxThresholdWatts { get; set; } = 0.05m;

    /// <summary>
    /// Quiet gap that ends a coalesced RF session (PACTOR burst gap). Default 6000 ms.
    /// Bursts closer than this merge into one Transmission History session.
    /// </summary>
    public int SessionCoalesceMs { get; set; } = 6000;

    /// <summary>
    /// Legacy alias for <see cref="SessionCoalesceMs"/>. Kept for saved settings / older installs.
    /// When SessionCoalesceMs is unset/invalid, EffectiveSessionCoalesceMs falls back here.
    /// </summary>
    public int TxEndDebounceMs { get; set; } = 6000;

    /// <summary>
    /// Minimum forward power (W) required before SWR / reflected contribute to session max/avg.
    /// Default 0.5 W — below this the LP-100A still reports SWR but coupler noise makes it unreliable.
    /// </summary>
    public decimal SwrMinForwardWatts { get; set; } = 0.5m;

    /// <summary>Record timestamped raw P responses under LogsPath (bounded file).</summary>
    public bool CaptureRawFrames { get; set; }

    public Lp100AlertOptions Alerts { get; set; } = new();

    /// <summary>Resolves coalesce timeout: prefer SessionCoalesceMs, else TxEndDebounceMs, else 6000.</summary>
    public int EffectiveSessionCoalesceMs
    {
        get
        {
            if (SessionCoalesceMs >= 100)
                return SessionCoalesceMs;
            if (TxEndDebounceMs >= 100)
                return TxEndDebounceMs;
            return 6000;
        }
    }
}

public sealed class Lp100AlertOptions
{
    public bool Enabled { get; set; }
    public bool HighSwr { get; set; } = true;
    public bool CriticalSwr { get; set; } = true;
    public bool HighReflected { get; set; } = true;
    public bool Disconnected { get; set; } = true;
    public bool Stale { get; set; } = true;
    public bool Recovery { get; set; } = true;
    public decimal SwrWarning { get; set; } = 2.0m;
    public decimal SwrCritical { get; set; } = 3.0m;
    public decimal ReflectedWarningWatts { get; set; } = 25m;
    public decimal? HighPowerWarningWatts { get; set; }
    public int CooldownMinutes { get; set; } = 5;
}

public sealed class RfMonitoringOptions
{
    public string TelemetryPath { get; set; } = @"C:\PWM\RfTelemetry.json";
    public string HistoryPath { get; set; } = @"C:\PWM\RfHistory.json";
    public string AnalysisPath { get; set; } = @"C:\PWM\RfAnalysis.json";
    public string SwrByFrequencyPath { get; set; } = @"C:\PWM\RfSwrByFrequency.json";
    public string TransmissionHistoryPath { get; set; } = @"C:\PWM\RfTransmissionHistory.json";
    public int HistorySampleSeconds { get; set; } = 5;
    public int StaleAfterSeconds { get; set; } = 10;
}

public static class Lp100MonitorLaunchSpec
{
    public static ProcessStartInfo Create(
        Lp100MonitorOptions options,
        string contentRootPath,
        bool demoMode = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        if (string.IsNullOrWhiteSpace(options.ExecutablePath))
            throw new InvalidOperationException("Lp100Monitor:ExecutablePath is required.");
        if (string.IsNullOrWhiteSpace(options.OutputPath))
            throw new InvalidOperationException("Lp100Monitor:OutputPath is required.");
        if (string.IsNullOrWhiteSpace(options.LogsPath))
            throw new InvalidOperationException("Lp100Monitor:LogsPath is required.");
        if (options.BaudRate <= 0)
            throw new InvalidOperationException("Lp100Monitor:BaudRate must be positive.");
        if (options.IntervalMs < 50)
            throw new InvalidOperationException("Lp100Monitor:IntervalMs must be >= 50.");

        var executablePath = Path.IsPathRooted(options.ExecutablePath)
            ? options.ExecutablePath
            : Path.Combine(contentRootPath, options.ExecutablePath);
        executablePath = Path.GetFullPath(executablePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? contentRootPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        void Add(string value) => startInfo.ArgumentList.Add(value);

        if (demoMode)
        {
            Add("--mock");
            Add("--force-demo");
        }
        else
        {
            var port = SerialPortName.Normalize(options.Port);
            if (!string.IsNullOrWhiteSpace(port))
            {
                Add("--port");
                Add(port);
            }
            if (!options.AutoDetect)
                Add("--no-auto-detect");
        }

        Add("--baud");
        Add(options.BaudRate.ToString());
        Add("--output");
        Add(Path.GetFullPath(options.OutputPath));
        Add("--logs");
        Add(Path.GetFullPath(options.LogsPath));
        Add("--interval-ms");
        Add(options.IntervalMs.ToString());
        Add("--idle-interval-ms");
        Add(Math.Max(250, options.IdleIntervalMs).ToString());
        if (options.CaptureRawFrames)
            Add("--capture");

        return startInfo;
    }
}
