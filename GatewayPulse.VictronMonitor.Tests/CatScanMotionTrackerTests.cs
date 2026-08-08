using GatewayPulse.Core;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class CatScanMotionTrackerTests
{
    private static readonly ScanChannel[] Channels =
    [
        new() { Number = 1, FrequencyHz = 7_101_500, FrequencyKhz = "7101.500" },
        new() { Number = 2, FrequencyHz = 10_141_500, FrequencyKhz = "10141.500" },
        new() { Number = 3, FrequencyHz = 14_101_500, FrequencyKhz = "14101.500" }
    ];

    [Fact]
    public void SuspendedScannerWithStaticCatFrequencyRemainsStopped()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        for (var i = 0; i < 6; i++)
            Assert.False(Observe(tracker, 7101.500m, start.AddSeconds(i * 2)));

        Assert.False(tracker.Recovered);
    }

    [Fact]
    public void SuspendedScannerRecoversAfterFourMatchedObservationsAndThreeTransitions()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        Assert.False(Observe(tracker, 7101.500m, start));
        Assert.False(Observe(tracker, 10141.500m, start.AddSeconds(2)));
        Assert.False(Observe(tracker, 14101.500m, start.AddSeconds(4)));
        Assert.True(Observe(tracker, 7101.500m, start.AddSeconds(6)));
        Assert.True(tracker.Recovered);
    }

    [Fact]
    public void ManualOffListFrequencyInvalidatesCandidateSequence()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        Assert.False(Observe(tracker, 7101.500m, start));
        Assert.False(Observe(tracker, 10141.500m, start.AddSeconds(2)));
        Assert.False(Observe(tracker, 7200.000m, start.AddSeconds(4)));
        Assert.False(Observe(tracker, 7101.500m, start.AddSeconds(6)));
        Assert.False(Observe(tracker, 10141.500m, start.AddSeconds(8)));
        Assert.False(Observe(tracker, 14101.500m, start.AddSeconds(10)));
        Assert.False(tracker.Recovered);
    }

    [Fact]
    public void SingleReadAndDuplicateTimestampNeverRecover()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        Assert.False(Observe(tracker, 7101.500m, start));
        Assert.False(Observe(tracker, 10141.500m, start));
        Assert.False(Observe(tracker, 14101.500m, start));
        Assert.False(Observe(tracker, 7101.500m, start));
        Assert.False(tracker.Recovered);
    }

    [Fact]
    public void MatchedTransitionsOutsideFifteenSecondWindowDoNotRecover()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        Assert.False(Observe(tracker, 7101.500m, start));
        Assert.False(Observe(tracker, 10141.500m, start.AddSeconds(6)));
        Assert.False(Observe(tracker, 14101.500m, start.AddSeconds(12)));
        Assert.False(Observe(tracker, 7101.500m, start.AddSeconds(18)));
        Assert.False(tracker.Recovered);
    }

    [Fact]
    public void MatchUsesCurrentFrequencyWithoutDialOffsetAndWithinOneHundredHz()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        Assert.False(Observe(tracker, 7101.600m, start));
        Assert.False(Observe(tracker, 10141.400m, start.AddSeconds(2)));
        Assert.False(Observe(tracker, 14101.600m, start.AddSeconds(4)));
        Assert.True(Observe(tracker, 7101.400m, start.AddSeconds(6)));

        var offsetTracker = new CatScanMotionTracker();
        Assert.False(Observe(offsetTracker, 7100.000m, start));
        Assert.False(offsetTracker.Recovered);
    }

    [Fact]
    public void StaleSamplesAndConfiguredChannelChangesResetState()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        Assert.False(tracker.Observe(7101.500m, start, Channels, start.AddSeconds(16)));
        Assert.False(Observe(tracker, 7101.500m, start.AddSeconds(20)));
        Assert.False(Observe(tracker, 10141.500m, start.AddSeconds(22)));

        tracker.SynchronizeChannels(
        [
            Channels[0],
            new ScanChannel { Number = 4, FrequencyHz = 18_101_500, FrequencyKhz = "18101.500" }
        ]);

        Assert.False(tracker.Recovered);
        Assert.False(tracker.Observe(
            18101.500m,
            start.AddSeconds(24),
            [Channels[0], new ScanChannel { FrequencyHz = 18_101_500 }],
            start.AddSeconds(24)));
    }

    private static bool Observe(
        CatScanMotionTracker tracker,
        decimal frequencyKhz,
        DateTimeOffset observedAt) =>
        tracker.Observe(frequencyKhz, observedAt, Channels, observedAt);
}
