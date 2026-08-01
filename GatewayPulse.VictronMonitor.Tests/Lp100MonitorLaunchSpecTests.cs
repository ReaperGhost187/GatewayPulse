using GatewayPulse.ServiceHosting;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class Lp100MonitorLaunchSpecTests
{
    [Fact]
    public void Create_IncludesPollFriendlyArgsAndNeverSendsControlCommands()
    {
        var start = Lp100MonitorLaunchSpec.Create(new Lp100MonitorOptions
        {
            Enabled = true,
            ExecutablePath = @"C:\temp\GatewayPulse.Lp100Monitor.exe",
            Port = "COM7",
            AutoDetect = false,
            BaudRate = 115200,
            OutputPath = @"C:\PWM\RfTelemetry.json",
            LogsPath = @"C:\PWM\logs",
            IntervalMs = 250,
            IdleIntervalMs = 1000
        }, @"C:\temp");

        var args = string.Join(' ', start.ArgumentList);
        Assert.Contains("--port", args);
        Assert.Contains("COM7", args);
        Assert.Contains("--no-auto-detect", args);
        Assert.Contains("115200", args);
        Assert.DoesNotContain(" A", " " + args);
        Assert.DoesNotContain("--alarm", args);
        Assert.DoesNotContain(" F", " " + args);
    }

    [Fact]
    public void Create_DemoModeUsesMockFlags()
    {
        var start = Lp100MonitorLaunchSpec.Create(new Lp100MonitorOptions
        {
            ExecutablePath = @"C:\temp\GatewayPulse.Lp100Monitor.exe",
            OutputPath = @"C:\PWM\RfTelemetry.json",
            LogsPath = @"C:\PWM\logs"
        }, @"C:\temp", demoMode: true);

        Assert.Contains("--mock", start.ArgumentList);
        Assert.Contains("--force-demo", start.ArgumentList);
    }
}
