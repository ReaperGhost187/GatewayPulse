using System.Diagnostics;

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
    public int IntervalMs { get; set; } = 250;
    public int IdleIntervalMs { get; set; } = 1000;
    public int RestartDelaySeconds { get; set; } = 10;
    public bool HistoryEnabled { get; set; } = true;
    /// <summary>Forward power (W) above this starts an RF transmission event.</summary>
    public decimal TxThresholdWatts { get; set; } = 0.05m;
    /// <summary>Power must stay below threshold this long to end a TX event.</summary>
    public int TxEndDebounceMs { get; set; } = 750;
    public Lp100AlertOptions Alerts { get; set; } = new();
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
        if (options.IntervalMs < 100)
            throw new InvalidOperationException("Lp100Monitor:IntervalMs must be >= 100.");

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
            if (!string.IsNullOrWhiteSpace(options.Port))
            {
                Add("--port");
                Add(options.Port.Trim());
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

        return startInfo;
    }
}
