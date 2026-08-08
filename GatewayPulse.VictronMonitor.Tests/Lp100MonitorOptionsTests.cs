using GatewayPulse.Lp100Monitor.CommandLine;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class Lp100MonitorOptionsTests
{
    [Fact]
    public void Parse_AllowsFiftyMsTxInterval()
    {
        var options = MonitorOptions.Parse([
            "--mock",
            "--interval-ms", "50",
            "--idle-interval-ms", "500",
            "--output", "RfTelemetry.json",
            "--logs", "logs"
        ]);

        Assert.Equal(50, options.IntervalMs);
        Assert.Equal(500, options.IdleIntervalMs);
    }

    [Fact]
    public void Parse_ClampsTxIntervalBelowFiftyToFifty()
    {
        var options = MonitorOptions.Parse([
            "--mock",
            "--interval-ms", "10",
            "--output", "RfTelemetry.json",
            "--logs", "logs"
        ]);

        Assert.Equal(50, options.IntervalMs);
    }

    [Fact]
    public void Parse_CaptureFlag()
    {
        var options = MonitorOptions.Parse([
            "--port", "COM3",
            "--capture",
            "--output", "RfTelemetry.json",
            "--logs", "logs"
        ]);

        Assert.True(options.CaptureRaw);
        Assert.Equal(80, options.IntervalMs);
    }

    [Fact]
    public void Parse_NormalizesBarePortNumber()
    {
        var options = MonitorOptions.Parse([
            "--port", "4",
            "--output", "RfTelemetry.json",
            "--logs", "logs"
        ]);

        Assert.Equal("COM4", options.Port);
    }
}
