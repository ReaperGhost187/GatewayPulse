namespace GatewayPulse.VictronMonitor.CommandLine;

public enum MonitorMode
{
    Help,
    Scan,
    Device,
    MultiDevice,
    Mock
}

public sealed class MonitorOptions
{
    public required MonitorMode Mode { get; init; }
    public required string OutputPath { get; init; }
    public required string LogsPath { get; init; }
    public string? Address { get; init; }
    public string? Name { get; init; }
    public byte[]? AdvertisementKey { get; init; }
    public string? ConfigurationPath { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);
    public bool Once { get; init; }
    public bool ForceDemo { get; init; }

    public static MonitorOptions Parse(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
            return Create(MonitorMode.Help);

        ValidateArguments(args);

        var modes = new[]
        {
            (Flag: "--scan", Mode: MonitorMode.Scan),
            (Flag: "--device", Mode: MonitorMode.Device),
            (Flag: "--multi-device", Mode: MonitorMode.MultiDevice),
            (Flag: "--mock", Mode: MonitorMode.Mock)
        }.Where(candidate => args.Contains(candidate.Flag, StringComparer.OrdinalIgnoreCase)).ToList();

        if (modes.Count != 1)
            throw new ArgumentException("Specify exactly one mode: --scan, --device, --multi-device, or --mock.");
        if (modes[0].Mode != MonitorMode.Device &&
            new[] { "--address", "--name", "--key-file" }.Any(option => args.Contains(option, StringComparer.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("--address, --name, and --key-file are valid only with --device mode.");
        }
        if (modes[0].Mode != MonitorMode.MultiDevice && args.Contains("--config", StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("--config is valid only with --multi-device mode.");
        if (modes[0].Mode == MonitorMode.Scan &&
            new[] { "--output", "--interval" }.Any(option => args.Contains(option, StringComparer.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("--output and --interval are not used by --scan mode.");
        }
        var forceDemo = args.Contains("--force-demo", StringComparer.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("GATEWAYPULSE_ALLOW_MOCK"), "1", StringComparison.Ordinal);
        if (forceDemo && modes[0].Mode != MonitorMode.Mock)
            throw new ArgumentException("--force-demo is valid only with --mock mode.");

        var address = GetValue(args, "--address");
        var name = GetValue(args, "--name");
        var keyFile = GetValue(args, "--key-file");
        var configurationFile = GetValue(args, "--config");
        string? keyText = null;
        if (modes[0].Mode == MonitorMode.Device)
        {
            try
            {
                keyText = keyFile is null
                    ? Environment.GetEnvironmentVariable("GATEWAYPULSE_VICTRON_KEY")
                    : File.ReadAllText(Path.GetFullPath(keyFile));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ArgumentException($"Unable to read the Victron advertisement key file: {ex.Message}");
            }
        }
        var intervalText = GetValue(args, "--interval");
        var output = GetValue(args, "--output") ?? Path.Combine(AppContext.BaseDirectory, "PowerTelemetry.json");
        var logs = GetValue(args, "--logs") ?? Path.Combine(AppContext.BaseDirectory, "logs");
        var interval = TimeSpan.FromSeconds(5);

        if (intervalText is not null)
        {
            if (!double.TryParse(intervalText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ||
                !double.IsFinite(seconds) || seconds <= 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
                throw new ArgumentException("--interval must be a positive number of seconds.");

            interval = TimeSpan.FromSeconds(seconds);
        }

        if (modes[0].Mode == MonitorMode.Device && string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("--device requires --address so Windows can open the BLE GATT device.");
        if (modes[0].Mode == MonitorMode.Device && !IsValidBluetoothAddress(address!))
            throw new ArgumentException("--address must be a 12-digit Bluetooth MAC address, for example AA:BB:CC:DD:EE:FF.");
        if (modes[0].Mode == MonitorMode.MultiDevice && string.IsNullOrWhiteSpace(configurationFile))
            throw new ArgumentException("--multi-device requires --config with the Gateway Pulse configuration file path.");

        var outputPath = Path.GetFullPath(output);
        var logsPath = Path.GetFullPath(logs);
        if (modes[0].Mode == MonitorMode.Mock &&
            !forceDemo &&
            IsProtectedProductionPath(outputPath))
        {
            throw new ArgumentException(
                "Refusing to write mock telemetry under C:\\PWM. Use a non-production --output path, or pass --force-demo / set GATEWAYPULSE_ALLOW_MOCK=1 for explicit Demo Mode.");
        }

        byte[]? key = null;
        if (!string.IsNullOrWhiteSpace(keyText))
        {
            var trimmedKey = keyText.Trim();
            if (trimmedKey.Length != 32 || !trimmedKey.All(char.IsAsciiHexDigit))
                throw new ArgumentException("The Victron advertisement key must be exactly 32 hexadecimal characters.");
            key = Convert.FromHexString(trimmedKey);
        }

        return new MonitorOptions
        {
            Mode = modes[0].Mode,
            OutputPath = outputPath,
            LogsPath = logsPath,
            Address = address,
            Name = name,
            AdvertisementKey = key,
            ConfigurationPath = configurationFile is null ? null : Path.GetFullPath(configurationFile),
            Interval = interval,
            Once = args.Contains("--once", StringComparer.OrdinalIgnoreCase),
            ForceDemo = forceDemo
        };
    }

    private static MonitorOptions Create(MonitorMode mode) => new()
    {
        Mode = mode,
        OutputPath = Path.Combine(AppContext.BaseDirectory, "PowerTelemetry.json"),
        LogsPath = Path.Combine(AppContext.BaseDirectory, "logs")
    };

    private static string? GetValue(string[] args, string option)
    {
        var index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return null;
        if (index == args.Length - 1 || args[index + 1].StartsWith('-'))
            throw new ArgumentException($"{option} requires a value.");
        return args[index + 1];
    }

    private static void ValidateArguments(string[] args)
    {
        string[] flags = ["--scan", "--device", "--multi-device", "--mock", "--once", "--force-demo"];
        string[] valueOptions = ["--address", "--name", "--key-file", "--config", "--interval", "--output", "--logs"];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            if (!seen.Add(args[index]))
                throw new ArgumentException($"Duplicate command-line option: {args[index]}.");
            if (flags.Contains(args[index], StringComparer.OrdinalIgnoreCase))
                continue;
            if (valueOptions.Contains(args[index], StringComparer.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            throw new ArgumentException($"Unknown command-line option or value: {args[index]}.");
        }
    }

    private static bool IsValidBluetoothAddress(string address)
    {
        var hexDigits = address.Where(char.IsAsciiHexDigit).ToArray();
        return hexDigits.Length == 12 &&
               address.All(character => char.IsAsciiHexDigit(character) || character is ':' or '-');
    }

    internal static bool IsProtectedProductionPath(string path)
    {
        var full = Path.GetFullPath(path);
        var pwm = Path.GetFullPath(@"C:\PWM");
        return full.Equals(pwm, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(pwm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(pwm + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
