using GatewayPulse.ServiceHosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class VictronMonitorSupervisorTests
{
    [Fact]
    public async Task ProcessLauncher_DisposeKillsCollectorProcessTree()
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec")!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");
        var launcher = new VictronMonitorProcessLauncher();
        var process = launcher.Start(startInfo);
        var processId = process.Id;

        await process.DisposeAsync();

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (IsRunning(processId) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.False(IsRunning(processId));
    }

    [Fact]
    public void AddVictronMonitorSupervision_RegistersSupervisorAndOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VictronMonitor:Enabled"] = "true",
                ["VictronMonitor:Address"] = "D5:11:30:C1:55:16"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment());

        services.AddVictronMonitorSupervision(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<IOptions<VictronMonitorOptions>>().Value.Enabled);
        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service is VictronMonitorSupervisor);
    }

    [Fact]
    public async Task ExitedCollector_IsRestartedUntilServiceStops()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"gatewaypulse-supervisor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var executablePath = Path.Combine(testDirectory, "GatewayPulse.VictronMonitor.exe");
        var keyPath = Path.Combine(testDirectory, "victron.key");
        await File.WriteAllTextAsync(executablePath, "test");
        await File.WriteAllTextAsync(keyPath, "test");
        var launcher = new ImmediatelyExitingLauncher();
        var options = Options.Create(new VictronMonitorOptions
        {
            Enabled = true,
            ExecutablePath = executablePath,
            Address = "D5:11:30:C1:55:16",
            KeyFile = keyPath,
            OutputPath = Path.Combine(testDirectory, "PowerTelemetry.json"),
            LogsPath = Path.Combine(testDirectory, "logs"),
            RestartDelaySeconds = 0
        });
        var supervisor = new VictronMonitorSupervisor(
            options,
            Options.Create(new DashboardOptions { DemoMode = false }),
            new FakeHostEnvironment(),
            launcher,
            NullLogger<VictronMonitorSupervisor>.Instance);

        try
        {
            await supervisor.StartAsync(CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (launcher.StartCount < 3 && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            await supervisor.StopAsync(CancellationToken.None);

            Assert.True(launcher.StartCount >= 3);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MultiDeviceCollector_StartsEvenWhenOptionalSmartShuntKeyIsNotPresent()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"gatewaypulse-supervisor-multi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var executablePath = Path.Combine(testDirectory, "GatewayPulse.VictronMonitor.exe");
        var configurationPath = Path.Combine(testDirectory, "appsettings.json");
        await File.WriteAllTextAsync(executablePath, "test");
        await File.WriteAllTextAsync(configurationPath, "{\"VictronMonitor\":{\"Devices\":[]}}");
        var launcher = new ImmediatelyExitingLauncher();
        var options = Options.Create(new VictronMonitorOptions
        {
            Enabled = true,
            ExecutablePath = executablePath,
            ConfigurationPath = configurationPath,
            Devices =
            [
                new VictronDeviceOptions
                {
                    Type = "BatteryProtect",
                    Address = "D5:11:30:C1:55:16",
                    KeyFile = Path.Combine(testDirectory, "victron.key"),
                    Enabled = true
                },
                new VictronDeviceOptions
                {
                    Type = "SmartShunt",
                    Address = "",
                    KeyFile = Path.Combine(testDirectory, "smartshunt.key"),
                    Enabled = false
                }
            ],
            OutputPath = Path.Combine(testDirectory, "PowerTelemetry.json"),
            LogsPath = Path.Combine(testDirectory, "logs"),
            RestartDelaySeconds = 0
        });
        var supervisor = new VictronMonitorSupervisor(
            options,
            Options.Create(new DashboardOptions { DemoMode = false }),
            new FakeHostEnvironment(),
            launcher,
            NullLogger<VictronMonitorSupervisor>.Instance);

        try
        {
            await supervisor.StartAsync(CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (launcher.StartCount == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            await supervisor.StopAsync(CancellationToken.None);

            Assert.True(launcher.StartCount > 0);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private sealed class ImmediatelyExitingLauncher : IVictronMonitorProcessLauncher
    {
        public int StartCount { get; private set; }

        public IVictronMonitorProcess Start(System.Diagnostics.ProcessStartInfo startInfo)
        {
            StartCount++;
            return new ImmediatelyExitingProcess();
        }
    }

    private sealed class ImmediatelyExitingProcess : IVictronMonitorProcess
    {
        public int Id => 1;
        public int ExitCode => 17;
        public bool HasExited => true;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Kill() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "GatewayPulse.Tests";
        public string ContentRootPath { get; set; } = @"C:\GatewayPulse";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
