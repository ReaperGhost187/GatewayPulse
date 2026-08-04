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
        Assert.Contains("--interval-ms", args);
        Assert.Contains("250", args);
        Assert.DoesNotContain(" A", " " + args);
        Assert.DoesNotContain("--alarm", args);
        Assert.DoesNotContain(" F", " " + args);
    }

    [Fact]
    public void Create_AllowsFiftyMsTxInterval()
    {
        var start = Lp100MonitorLaunchSpec.Create(new Lp100MonitorOptions
        {
            ExecutablePath = @"C:\temp\GatewayPulse.Lp100Monitor.exe",
            OutputPath = @"C:\PWM\RfTelemetry.json",
            LogsPath = @"C:\PWM\logs",
            IntervalMs = 50,
            IdleIntervalMs = 500
        }, @"C:\temp");

        var args = start.ArgumentList.ToList();
        var intervalIndex = args.IndexOf("--interval-ms");
        Assert.True(intervalIndex >= 0);
        Assert.Equal("50", args[intervalIndex + 1]);
        var idleIndex = args.IndexOf("--idle-interval-ms");
        Assert.True(idleIndex >= 0);
        Assert.Equal("500", args[idleIndex + 1]);
    }

    [Fact]
    public void Create_RejectsIntervalBelowFiftyMs()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Lp100MonitorLaunchSpec.Create(new Lp100MonitorOptions
            {
                ExecutablePath = @"C:\temp\GatewayPulse.Lp100Monitor.exe",
                OutputPath = @"C:\PWM\RfTelemetry.json",
                LogsPath = @"C:\PWM\logs",
                IntervalMs = 49
            }, @"C:\temp"));

        Assert.Contains(">= 50", error.Message);
    }

    [Fact]
    public void Options_DefaultSessionCoalesceIsSixSeconds()
    {
        var options = new Lp100MonitorOptions();
        Assert.Equal(6000, options.SessionCoalesceMs);
        Assert.Equal(6000, options.TxEndDebounceMs);
        Assert.Equal(6000, options.EffectiveSessionCoalesceMs);
        Assert.Equal(0.5m, options.SwrMinForwardWatts);
        Assert.Equal(80, options.IntervalMs);
        Assert.False(options.CaptureRawFrames);
    }

    [Fact]
    public void Create_IncludesCaptureFlagWhenEnabled()
    {
        var start = Lp100MonitorLaunchSpec.Create(new Lp100MonitorOptions
        {
            ExecutablePath = @"C:\temp\GatewayPulse.Lp100Monitor.exe",
            OutputPath = @"C:\PWM\RfTelemetry.json",
            LogsPath = @"C:\PWM\logs",
            CaptureRawFrames = true
        }, @"C:\temp");

        Assert.Contains("--capture", start.ArgumentList);
    }

    [Fact]
    public void EffectiveSessionCoalesce_FallsBackToLegacyDebounce()
    {
        var options = new Lp100MonitorOptions
        {
            SessionCoalesceMs = 0,
            TxEndDebounceMs = 2500
        };
        Assert.Equal(2500, options.EffectiveSessionCoalesceMs);
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
