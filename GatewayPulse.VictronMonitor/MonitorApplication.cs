using GatewayPulse.PowerMonitoring;
using GatewayPulse.VictronMonitor.Bluetooth;
using GatewayPulse.VictronMonitor.CommandLine;
using GatewayPulse.VictronMonitor.Configuration;
using GatewayPulse.VictronMonitor.Logging;
using GatewayPulse.VictronMonitor.Providers;
using System.Security.Cryptography;

namespace GatewayPulse.VictronMonitor;

public static class MonitorApplication
{
    public static async Task<int> RunAsync(MonitorOptions options, CancellationToken cancellationToken = default)
    {
        if (options.Mode == MonitorMode.Help)
            return ShowHelp();

        MonitorLogger? logger = null;
        try
        {
            logger = new MonitorLogger(options.LogsPath);
            logger.Info($"GatewayPulse.VictronMonitor starting in {options.Mode} mode.");
            return options.Mode switch
            {
                MonitorMode.Mock => await RunMockAsync(options, logger, cancellationToken),
                MonitorMode.Scan => await RunScanAsync(options, logger, cancellationToken),
                MonitorMode.Device => await RunDeviceAsync(options, logger, cancellationToken),
                MonitorMode.MultiDevice => await RunMultiDeviceAsync(options, logger, cancellationToken),
                _ => throw new NotSupportedException($"Unknown monitor mode: {options.Mode}.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger?.Info("Shutdown requested.");
            return 0;
        }
        catch (Exception ex)
        {
            if (logger is null)
                Console.Error.WriteLine($"Unable to initialize GatewayPulse.VictronMonitor logging: {ex.Message}");
            else
                logger.Error($"Fatal error: {ex}");
            return 1;
        }
    }

    private static async Task<int> RunMockAsync(
        MonitorOptions options,
        MonitorLogger logger,
        CancellationToken cancellationToken)
    {
        IPowerMonitor provider = new MockPowerProvider();
        await provider.ConnectAsync();
        logger.Info($"Connected provider: {provider.DeviceName}");

        do
        {
            var telemetry = await provider.GetTelemetryAsync();
            await PowerTelemetryJson.WriteFileAtomicallyAsync(options.OutputPath, telemetry, cancellationToken);
            Console.WriteLine(PowerTelemetryJson.Serialize(telemetry));
            logger.Info($"Wrote mock telemetry to {options.OutputPath}.");

            if (!options.Once)
                await Task.Delay(options.Interval, cancellationToken);
        }
        while (!options.Once);

        await provider.DisconnectAsync();
        return 0;
    }

    private static async Task<int> RunScanAsync(
        MonitorOptions options,
        MonitorLogger logger,
        CancellationToken cancellationToken)
    {
        using var scanLog = new ScanLogWriter(options.LogsPath);
        using var source = new WindowsBleAdvertisementSource();
        var firstRecord = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.StatusChanged += (_, message) => logger.Warning(message);

        source.ScanRecordReceived += (_, record) =>
        {
            scanLog.Write(record);
            logger.Info($"BLE {record.Address} name='{record.DeviceName}' RSSI={record.Rssi} connectable={record.Connectable?.ToString() ?? "unknown"} raw={record.RawAdvertisementBytes}");
            firstRecord.TrySetResult();
        };

        logger.Info($"Writing complete scan records to {scanLog.Path}.");
        await source.StartAsync();
        logger.Info("BLE active scan started. Press Ctrl+C to stop.");

        if (options.Once)
            await firstRecord.Task.WaitAsync(cancellationToken);
        else
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        await source.StopAsync();
        return 0;
    }

    private static async Task<int> RunDeviceAsync(
        MonitorOptions options,
        MonitorLogger logger,
        CancellationToken cancellationToken)
    {
        using var scanLog = new ScanLogWriter(options.LogsPath);
        using var source = new WindowsBleAdvertisementSource();
        var address = options.Address!;
        source.StatusChanged += (_, message) => logger.Warning(message);

        source.ScanRecordReceived += (_, record) =>
        {
            if (NormalizeAddress(record.Address) == NormalizeAddress(address))
                scanLog.Write(record);
        };

        logger.Info("Running a bounded GATT diagnostic session before passive advertisement monitoring.");
        await RunBoundedGattDiagnosticsAsync(
            token => GattDeviceInspector.RunReconnectLoopAsync(address, logger, options.Interval, token),
            TimeSpan.FromSeconds(10),
            cancellationToken);
        logger.Info("GATT diagnostics finished; starting passive Instant Readout monitoring.");

        VictronBatteryProtectProvider? provider = null;
        if (options.AdvertisementKey is not null)
        {
            var suppliedKey = options.AdvertisementKey;
            try
            {
                provider = new VictronBatteryProtectProvider(
                    source,
                    suppliedKey,
                    targetAddress: address,
                    targetName: options.Name);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(suppliedKey);
            }
            try
            {
                await provider.ConnectAsync();
            }
            catch
            {
                provider.Dispose();
                throw;
            }
            logger.Info("Victron Instant Readout decoder started; advertisements provide the dashboard telemetry.");
        }
        else
        {
            await source.StartAsync();
            logger.Warning("No advertisement key was supplied. GATT was inspected, but encrypted Instant Readout telemetry cannot be decoded.");
        }

        try
        {
            do
            {
                var telemetry = provider is null
                    ? new PowerTelemetry
                    {
                        Connected = false,
                        Provider = "victron-batteryprotect",
                        Device = options.Name ?? "Victron Smart BatteryProtect",
                        DeviceId = address,
                        LastUpdate = DateTimeOffset.UtcNow,
                        Error = "Advertisement key required to decode Victron Instant Readout telemetry."
                    }
                    : options.Once
                        ? await WaitForConnectedTelemetryAsync(
                            provider,
                            TimeSpan.FromSeconds(15),
                            TimeSpan.FromMilliseconds(100),
                            cancellationToken)
                        : await provider.GetTelemetryAsync();

                await PowerTelemetryJson.WriteFileAtomicallyAsync(options.OutputPath, telemetry, cancellationToken);
                Console.WriteLine(PowerTelemetryJson.Serialize(telemetry));
                logger.Info($"Wrote device telemetry to {options.OutputPath}; connected={telemetry.Connected}; error={telemetry.Error ?? "none"}.");

                if (!options.Once)
                    await Task.Delay(options.Interval, cancellationToken);
            }
            while (!options.Once);
        }
        finally
        {
            if (provider is not null)
            {
                await provider.DisconnectAsync();
                provider.Dispose();
            }
            else
                await source.StopAsync();
        }

        return 0;
    }

    private static async Task<int> RunMultiDeviceAsync(
        MonitorOptions options,
        MonitorLogger logger,
        CancellationToken cancellationToken)
    {
        using var configuration = VictronMultiDeviceConfiguration.Load(options.ConfigurationPath!);
        var providers = new List<IPowerProvider>();
        var unavailable = new List<PowerDeviceTelemetry>();
        foreach (var device in configuration.Devices)
        {
            if (!device.IsUsable)
            {
                logger.Warning($"{device.Type} configuration is unavailable: {device.Error}");
                unavailable.Add(new PowerDeviceTelemetry
                {
                    Type = device.Type,
                    Provider = device.Type == PowerDeviceTypes.SmartShunt ? "victron-smartshunt" : "victron-batteryprotect",
                    Connected = false,
                    ConnectionState = PowerConnectionStates.Misconfigured,
                    Device = device.Type,
                    DeviceId = FormatAddress(device.Address),
                    Error = device.Error
                });
                continue;
            }

            providers.Add(device.Type switch
            {
                PowerDeviceTypes.BatteryProtect => new BatteryProtectDecoder(device.Address, device.AdvertisementKey!),
                PowerDeviceTypes.SmartShunt => new SmartShuntDecoder(device.Address, device.AdvertisementKey!),
                _ => throw new InvalidDataException("Unsupported Victron device type passed configuration validation.")
            });
        }

        using var source = new WindowsBleAdvertisementSource();
        source.StatusChanged += (_, message) => logger.Warning(message);

        using var manager = new VictronPowerManager(
            source,
            providers,
            configuration.StaleAfter,
            configuration.Thresholds,
            unavailableDevices: unavailable);
        await manager.ConnectAsync();
        if (providers.Count > 0)
            logger.Info($"Shared BLE scanner started for {providers.Count} usable Victron power device(s).");
        else
            logger.Warning("No usable Victron device configuration is available; BLE scanning remains idle.");

        try
        {
            do
            {
                var telemetry = options.Once && providers.Count > 0
                    ? await WaitForConnectedTelemetryAsync(
                        manager,
                        TimeSpan.FromSeconds(15),
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    : await manager.GetTelemetryAsync();
                await PowerTelemetryJson.WriteFileAtomicallyAsync(options.OutputPath, telemetry, cancellationToken);
                Console.WriteLine(PowerTelemetryJson.Serialize(telemetry));
                logger.Info($"Wrote schema-v2 power telemetry; connected devices={telemetry.Devices.Count(device => device.Connected)}.");
                if (!options.Once)
                    await Task.Delay(options.Interval, cancellationToken);
            }
            while (!options.Once);
        }
        finally
        {
            await manager.DisconnectAsync();
        }
        return 0;
    }

    private static async Task RunBoundedGattDiagnosticsAsync(
        Func<CancellationToken, Task> diagnostic,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var diagnosticCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        diagnosticCancellation.CancelAfter(timeout);
        try
        {
            await diagnostic(diagnosticCancellation.Token);
        }
        catch (OperationCanceledException) when (
            diagnosticCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task<PowerTelemetry> WaitForConnectedTelemetryAsync(
        IPowerMonitor provider,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        var deadline = System.Diagnostics.Stopwatch.GetTimestamp() +
            (long)(timeout.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        PowerTelemetry telemetry;

        do
        {
            telemetry = await provider.GetTelemetryAsync();
            if (telemetry.Connected)
                return telemetry;

            await Task.Delay(pollInterval, cancellationToken);
        }
        while (System.Diagnostics.Stopwatch.GetTimestamp() < deadline);

        return telemetry;
    }

    private static string NormalizeAddress(string address) =>
        new(address.Where(char.IsAsciiHexDigit).ToArray());

    private static string FormatAddress(string address)
    {
        var normalized = NormalizeAddress(address).ToUpperInvariant();
        return normalized.Length == 12
            ? string.Join(':', Enumerable.Range(0, 6).Select(index => normalized.Substring(index * 2, 2)))
            : normalized;
    }

    private static int ShowHelp()
    {
        Console.WriteLine("""
            GatewayPulse.VictronMonitor

              --scan                         Log BLE advertisements until Ctrl+C
              --device --address <MAC>        Inspect GATT and monitor BatteryProtect data
                       [--key-file <path>]     File containing the Instant Readout key
              --multi-device --config <path>  Monitor configured BatteryProtect/SmartShunt devices
              --mock                         Write realistic test telemetry every five seconds (dev only)

            Options:
              --logs <folder>    Log folder for every mode (default: logs beside the executable)
              --output <path>    Mock/device JSON path (default: PowerTelemetry.json beside the executable)
              --interval <sec>   Mock/device output/reconnect interval (default: 5)
              --once             Produce one mock/device sample, then exit; device waits up to 15 seconds
              --force-demo       Allow --mock to write under C:\\PWM (explicit Demo Mode only)

            The key may also be supplied through GATEWAYPULSE_VICTRON_KEY.
            Direct command-line key values are intentionally not accepted.
            Mock mode refuses C:\\PWM unless --force-demo or GATEWAYPULSE_ALLOW_MOCK=1 is set.
            """);
        return 0;
    }
}
