using System.Text;
using GatewayPulse.Lp100Monitor.Protocol;
using GatewayPulse.RfMonitoring;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class Lp100FrameParserTests
{
    // Official manual example (TelePost LP-100A Op Manual software section).
    private const string ManualExample = "1457.00,49.3,005.0,2,N8LP ,0,2,61.6,1.02";

    [Fact]
    public void TryParse_ManualExample_Succeeds()
    {
        Assert.True(Lp100FrameParser.TryParse(ManualExample, out var frame));
        Assert.Equal(1457.00m, frame.ForwardPowerWatts);
        Assert.Equal(49.3m, frame.ImpedanceOhms);
        Assert.Equal(5.0m, frame.PhaseDegrees);
        Assert.Equal(2, frame.AlarmIndex);
        Assert.Equal("N8LP", frame.Callsign);
        Assert.Equal(0, frame.PowerRange);
        Assert.Equal(2, frame.MeterMode);
        Assert.Equal(61.6m, frame.Dbm);
        Assert.Equal(1.02m, frame.Swr);
    }

    [Fact]
    public void ExtractFrameBodies_HandlesPartialThenComplete()
    {
        var buffer = new StringBuilder();
        buffer.Append(";1457.00,49.3");
        Assert.Empty(Lp100FrameParser.ExtractFrameBodies(buffer));

        buffer.Append(",005.0,2,N8LP ,0,2,61.6,1.02");
        var bodies = Lp100FrameParser.ExtractFrameBodies(buffer);
        Assert.Single(bodies);
        Assert.True(Lp100FrameParser.TryParse(bodies[0], out _));
    }

    [Fact]
    public void ExtractFrameBodies_HandlesMultipleFramesInOneRead()
    {
        var buffer = new StringBuilder();
        buffer.Append($";{ManualExample};10.0,50.0,0.0,0,TEST  ,1,0,40.0,1.10");
        var bodies = Lp100FrameParser.ExtractFrameBodies(buffer);
        Assert.Equal(2, bodies.Count);
    }

    [Fact]
    public void TryParse_RejectsCorruptFrames()
    {
        Assert.False(Lp100FrameParser.TryParse("bad", out _));
        Assert.False(Lp100FrameParser.TryParse("1,2,3", out _));
        Assert.False(Lp100FrameParser.TryParse("1,2,3,4,CALL,5,6,7,0.5", out _)); // SWR < 1
    }

    [Fact]
    public void DerivedMetrics_MatchTrustedFormulas()
    {
        var reflected = RfDerivedMetrics.ReflectedPowerWatts(100m, 2.0m);
        Assert.NotNull(reflected);
        Assert.InRange(reflected.Value, 11.0m, 12.0m); // ((2-1)/(2+1))^2 * 100 ≈ 11.11

        var rl = RfDerivedMetrics.ReturnLossDb(2.0m);
        Assert.NotNull(rl);
        Assert.InRange(rl.Value, 9.5m, 9.6m);

        var r = RfDerivedMetrics.ResistanceOhms(50m, 0m);
        Assert.Equal(50m, r);
        var x = RfDerivedMetrics.ReactanceOhms(50m, 90m);
        Assert.NotNull(x);
        Assert.InRange(x.Value, 49.9m, 50.1m);
    }
}
