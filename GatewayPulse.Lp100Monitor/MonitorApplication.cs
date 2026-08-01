using GatewayPulse.Lp100Monitor.CommandLine;
using GatewayPulse.Lp100Monitor.Logging;
using GatewayPulse.Lp100Monitor.Providers;
using GatewayPulse.RfMonitoring;

namespace GatewayPulse.Lp100Monitor;

public static class MonitorApplication
{
    public static async Task<int> RunAsync(MonitorOptions options, CancellationToken cancellationToken)
    {
        if (options.Mode == MonitorMode.Help)
        {
            Console.WriteLine(MonitorOptions.HelpText);
            return 0;
        }

        if (options.Mode == MonitorMode.Mock)
        {
            var fullOutput = Path.GetFullPath(options.OutputPath);
            if (fullOutput.StartsWith(@"C:\PWM", StringComparison.OrdinalIgnoreCase) &&
                !options.ForceDemo &&
                !string.Equals(Environment.GetEnvironmentVariable("GATEWAYPULSE_ALLOW_MOCK"), "1", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Mock mode refuses to write under C:\\PWM without --force-demo.");
                return 2;
            }
        }

        Directory.CreateDirectory(options.LogsPath);
        var logger = new MonitorLogger(options.LogsPath);
        IRfMonitor monitor = options.Mode == MonitorMode.Mock
            ? new MockRfProvider()
            : new TelePostLp100Provider(options.Port, options.AutoDetect, options.BaudRate);

        logger.Info($"Starting LP-100A monitor mode={options.Mode} port={options.Port ?? "(auto)"} baud={options.BaudRate}");

        if (options.Mode == MonitorMode.Test && monitor is TelePostLp100Provider provider)
        {
            var test = await provider.TestConnectionAsync(cancellationToken);
            await RfTelemetryJson.WriteFileAtomicallyAsync(options.OutputPath, test, cancellationToken);
            Console.WriteLine(RfTelemetryJson.Serialize(test));
            return test.Connected ? 0 : 1;
        }

        if (options.Once)
        {
            var once = await monitor.GetTelemetryAsync();
            await RfTelemetryJson.WriteFileAtomicallyAsync(options.OutputPath, once, cancellationToken);
            return once.Connected || options.Mode == MonitorMode.Mock ? 0 : 1;
        }

        var lastConnected = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var telemetry = await monitor.GetTelemetryAsync();
                await RfTelemetryJson.WriteFileAtomicallyAsync(options.OutputPath, telemetry, cancellationToken);

                if (telemetry.Connected != lastConnected)
                {
                    logger.Info(telemetry.Connected
                        ? $"LP-100A connected on {telemetry.ComPort}"
                        : $"LP-100A disconnected: {telemetry.Error}");
                    lastConnected = telemetry.Connected;
                }

                var delay = telemetry.Transmitting ? options.IntervalMs : options.IdleIntervalMs;
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.Error(ex.Message);
                await Task.Delay(options.IdleIntervalMs, cancellationToken);
            }
        }

        await monitor.DisconnectAsync();
        logger.Info("LP-100A monitor stopped.");
        return 0;
    }
}
