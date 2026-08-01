using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GatewayPulse.ServiceHosting;

public sealed class VictronMonitorOptions
{
    public bool Enabled { get; set; }
    public string ExecutablePath { get; set; } = @"VictronMonitor\GatewayPulse.VictronMonitor.exe";
    public string Address { get; set; } = "";
    public string KeyFile { get; set; } = @"C:\PWM\victron.key";
    public string OutputPath { get; set; } = @"C:\PWM\PowerTelemetry.json";
    public string LogsPath { get; set; } = @"C:\PWM\logs";
    public int IntervalSeconds { get; set; } = 5;
    public int RestartDelaySeconds { get; set; } = 10;
    public string ConfigurationPath { get; set; } = "appsettings.json";
    public List<VictronDeviceOptions> Devices { get; set; } = [];
    public PowerThresholdOptions Thresholds { get; set; } = new();
}

public sealed class VictronDeviceOptions
{
    public string Type { get; set; } = "";
    public string Address { get; set; } = "";
    public string KeyFile { get; set; } = "";
    public bool Enabled { get; set; }
}

public sealed class PowerThresholdOptions
{
    public int StaleAfterSeconds { get; set; } = 30;
    public int WeakSignalRssi { get; set; } = -85;
    public decimal StateOfChargeWarningPercent { get; set; } = 30m;
    public decimal StateOfChargeCriticalPercent { get; set; } = 15m;
    public decimal IdleCurrentAmps { get; set; } = 0.2m;
    public decimal LowVoltageWarning { get; set; } = 11.8m;
    public decimal LowVoltageCritical { get; set; } = 11.0m;
    public decimal HighVoltageWarning { get; set; } = 15.0m;
}

public static partial class VictronMonitorLaunchSpec
{
    public static ProcessStartInfo Create(
        VictronMonitorOptions options,
        string contentRootPath,
        bool demoMode = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        if (string.IsNullOrWhiteSpace(options.ExecutablePath))
            throw new InvalidOperationException("VictronMonitor:ExecutablePath is required.");

        if (string.IsNullOrWhiteSpace(options.OutputPath))
            throw new InvalidOperationException("VictronMonitor:OutputPath is required.");
        if (string.IsNullOrWhiteSpace(options.LogsPath))
            throw new InvalidOperationException("VictronMonitor:LogsPath is required.");
        if (options.IntervalSeconds <= 0)
            throw new InvalidOperationException("VictronMonitor:IntervalSeconds must be positive.");

        var executablePath = Path.IsPathRooted(options.ExecutablePath)
            ? options.ExecutablePath
            : Path.Combine(contentRootPath, options.ExecutablePath);
        executablePath = Path.GetFullPath(executablePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? contentRootPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = false,
            RedirectStandardOutput = false
        };

        if (demoMode)
        {
            Add("--mock");
            Add("--force-demo");
        }
        else if (options.Devices.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(options.ConfigurationPath))
                throw new InvalidOperationException("VictronMonitor:ConfigurationPath is required for multi-device monitoring.");
            var configurationPath = Path.IsPathRooted(options.ConfigurationPath)
                ? options.ConfigurationPath
                : Path.Combine(contentRootPath, options.ConfigurationPath);
            Add("--multi-device");
            Add("--config");
            Add(Path.GetFullPath(configurationPath));
        }
        else
        {
            if (!BluetoothAddressPattern().IsMatch(options.Address))
                throw new InvalidOperationException("VictronMonitor:Address must be a six-byte Bluetooth address separated by colons.");
            if (string.IsNullOrWhiteSpace(options.KeyFile))
                throw new InvalidOperationException("VictronMonitor:KeyFile is required.");
            Add("--device");
            Add("--address");
            Add(options.Address);
            Add("--key-file");
            Add(Path.GetFullPath(options.KeyFile));
        }
        Add("--output");
        Add(Path.GetFullPath(options.OutputPath));
        Add("--logs");
        Add(Path.GetFullPath(options.LogsPath));
        Add("--interval");
        Add(options.IntervalSeconds.ToString(CultureInfo.InvariantCulture));
        return startInfo;

        void Add(string value) => startInfo.ArgumentList.Add(value);
    }

    [GeneratedRegex("^[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}$", RegexOptions.CultureInvariant)]
    private static partial Regex BluetoothAddressPattern();
}
