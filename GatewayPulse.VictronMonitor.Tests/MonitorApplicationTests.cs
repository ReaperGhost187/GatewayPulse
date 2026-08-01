using GatewayPulse.VictronMonitor.CommandLine;
using GatewayPulse.PowerMonitoring;
using GatewayPulse.VictronMonitor.Logging;
using System.Reflection;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class MonitorApplicationTests
{
    [Fact]
    public async Task MockOnce_WritesTelemetryAndActivityLog()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gateway-pulse-app-{Guid.NewGuid():N}");
        var output = Path.Combine(directory, "PowerTelemetry.json");
        var logs = Path.Combine(directory, "logs");

        try
        {
            var exitCode = await MonitorApplication.RunAsync(new MonitorOptions
            {
                Mode = MonitorMode.Mock,
                OutputPath = output,
                LogsPath = logs,
                Once = true
            });

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(output));
            Assert.True(PowerTelemetryJson.Deserialize(await File.ReadAllTextAsync(output))!.Connected);
            Assert.NotEmpty(Directory.GetFiles(logs, "victron-monitor-*.log"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Help_DoesNotCreateLogFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gateway-pulse-help-{Guid.NewGuid():N}");

        var exitCode = await MonitorApplication.RunAsync(new MonitorOptions
        {
            Mode = MonitorMode.Help,
            OutputPath = Path.Combine(directory, "PowerTelemetry.json"),
            LogsPath = Path.Combine(directory, "logs")
        });

        Assert.Equal(0, exitCode);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Logger_EscapesControlCharactersFromBleFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gateway-pulse-log-{Guid.NewGuid():N}");
        try
        {
            var logger = new MonitorLogger(directory);
            logger.Info("device\r\n2026-01-01T00:00:00Z [INFO] forged\u001b");

            var lines = File.ReadAllLines(logger.LogFile);
            Assert.Single(lines);
            Assert.Contains("\\u000D\\u000A", lines[0]);
            Assert.Contains("\\u001B", lines[0]);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Logger_FileWriteFailure_DoesNotEscapeCallback()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gateway-pulse-log-failure-{Guid.NewGuid():N}");
        try
        {
            var logger = new MonitorLogger(directory);
            Directory.CreateDirectory(logger.LogFile);

            var error = Record.Exception(() => logger.Info("BLE callback message"));

            Assert.Null(error);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeviceOnce_WaitsForConnectedAdvertisementTelemetry()
    {
        var monitor = new ConnectingOnThirdReadMonitor();
        var method = typeof(MonitorApplication).GetMethod(
            "WaitForConnectedTelemetryAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task<PowerTelemetry>>(method.Invoke(
            null,
            [monitor, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), CancellationToken.None]));

        var telemetry = await task;

        Assert.True(telemetry.Connected);
        Assert.Equal(3, monitor.ReadCount);
    }

    [Fact]
    public async Task RunAsync_InvalidLogPath_ReturnsControlledFailure()
    {
        var options = new MonitorOptions
        {
            Mode = MonitorMode.Mock,
            OutputPath = Path.Combine(Path.GetTempPath(), "unused-power.json"),
            LogsPath = "invalid\0log-path",
            Once = true
        };

        var exitCode = await MonitorApplication.RunAsync(options);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task GattDiagnostics_AreCancelledBeforeAdvertisementMonitoringContinues()
    {
        var method = typeof(MonitorApplication).GetMethod(
            "RunBoundedGattDiagnosticsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var cancelled = false;
        async Task Diagnostic(CancellationToken token)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled = true;
                throw;
            }
        }

        var task = Assert.IsAssignableFrom<Task>(method.Invoke(
            null,
            [(Func<CancellationToken, Task>)Diagnostic, TimeSpan.FromMilliseconds(10), CancellationToken.None]));
        await task;

        Assert.True(cancelled);
    }

    private sealed class ConnectingOnThirdReadMonitor : IPowerMonitor
    {
        public int ReadCount { get; private set; }
        public bool IsConnected => ReadCount >= 3;
        public string DeviceName => "test";
        public Task<bool> ConnectAsync() => Task.FromResult(true);
        public Task DisconnectAsync() => Task.CompletedTask;

        public Task<PowerTelemetry> GetTelemetryAsync()
        {
            ReadCount++;
            return Task.FromResult(new PowerTelemetry
            {
                Connected = ReadCount >= 3,
                Provider = "test",
                Device = "test",
                LastUpdate = DateTimeOffset.UtcNow
            });
        }
    }
}
