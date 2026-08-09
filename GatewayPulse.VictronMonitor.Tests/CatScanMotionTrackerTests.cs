using GatewayPulse.Core;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class CatScanMotionTrackerTests
{
    private static readonly ScanChannel[] Channels =
    [
        new() { Number = 1, FrequencyHz = 4_882_000, FrequencyKhz = "4882.000" },
        new() { Number = 2, FrequencyHz = 7_101_500, FrequencyKhz = "7101.500" },
        new() { Number = 3, FrequencyHz = 10_141_500, FrequencyKhz = "10141.500" }
    ];

    [Fact]
    public void MatchConfiguredCenter_Center4882MatchesCivDial4880Point5()
    {
        Assert.Equal(
            4_882_000,
            CatScanMotionTracker.MatchConfiguredCenter(4_880_500, [4_882_000]));
        Assert.Equal(
            4_882_000,
            CatScanMotionTracker.MatchConfiguredCenter(4_882_000, [4_882_000]));
        Assert.Null(CatScanMotionTracker.MatchConfiguredCenter(7_200_000, [4_882_000]));
    }

    [Fact]
    public void PactorCenterDialConversionPreservesDashboardValues()
    {
        Assert.Equal(1_500, PactorFrequency.CenterToDialOffsetHz);
        Assert.Equal(4880.500m, PactorFrequency.CenterToDialKhz(4882.000m));
        Assert.Equal(4882.000m, PactorFrequency.DialToCenterKhz(4880.500m));
    }

    [Fact]
    public void SuspendedScannerWithStaticCatFrequencyRemainsStopped()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        // Static dial-basis observation for configured center 4882.000.
        for (var i = 0; i < 6; i++)
            Assert.False(Observe(tracker, 4880.500m, start.AddSeconds(i * 2)));

        Assert.False(tracker.Recovered);

        // Static center-basis observation (how Program overlays CI-V as Current/center).
        var centerTracker = new CatScanMotionTracker();
        for (var i = 0; i < 6; i++)
            Assert.False(Observe(centerTracker, 4882.000m, start.AddSeconds(i * 2)));

        Assert.False(centerTracker.Recovered);
    }

    [Fact]
    public void SuspendedScannerRecoversFromDialBasisChannelTransitions()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        // Dial observations for centers 4882.000 / 7101.500 / 10141.500.
        Assert.False(Observe(tracker, 4880.500m, start));
        Assert.False(Observe(tracker, 7100.000m, start.AddSeconds(2)));
        Assert.False(Observe(tracker, 10140.000m, start.AddSeconds(4)));
        Assert.True(Observe(tracker, 4880.500m, start.AddSeconds(6)));
        Assert.True(tracker.Recovered);
    }

    [Fact]
    public void SuspendedScannerRecoversFromCenterBasisChannelTransitions()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        // Same sequence expressed as the dashboard/Program "center" basis.
        Assert.False(Observe(tracker, 4882.000m, start));
        Assert.False(Observe(tracker, 7101.500m, start.AddSeconds(2)));
        Assert.False(Observe(tracker, 10141.500m, start.AddSeconds(4)));
        Assert.True(Observe(tracker, 4882.000m, start.AddSeconds(6)));
        Assert.True(tracker.Recovered);
    }

    [Fact]
    public void MixedCenterAndDialBasisTransitionsStillRecover()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        Assert.False(Observe(tracker, 4882.000m, start));          // center
        Assert.False(Observe(tracker, 7100.000m, start.AddSeconds(2))); // dial
        Assert.False(Observe(tracker, 10141.500m, start.AddSeconds(4))); // center
        Assert.True(Observe(tracker, 4880.500m, start.AddSeconds(6)));  // dial
        Assert.True(tracker.Recovered);
    }

    [Fact]
    public void ManualOffListFrequencyInvalidatesCandidateSequence()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        Assert.False(Observe(tracker, 4880.500m, start));
        Assert.False(Observe(tracker, 7100.000m, start.AddSeconds(2)));
        Assert.False(Observe(tracker, 7200.000m, start.AddSeconds(4)));
        Assert.False(Observe(tracker, 4880.500m, start.AddSeconds(6)));
        Assert.False(Observe(tracker, 7100.000m, start.AddSeconds(8)));
        Assert.False(Observe(tracker, 10140.000m, start.AddSeconds(10)));
        Assert.False(tracker.Recovered);
    }

    [Fact]
    public void SingleReadAndDuplicateTimestampNeverRecover()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        Assert.False(Observe(tracker, 4880.500m, start));
        Assert.False(Observe(tracker, 7100.000m, start));
        Assert.False(Observe(tracker, 10140.000m, start));
        Assert.False(Observe(tracker, 4880.500m, start));
        Assert.False(tracker.Recovered);
    }

    [Fact]
    public void MatchedTransitionsOutsideFifteenSecondWindowDoNotRecover()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        Assert.False(Observe(tracker, 4880.500m, start));
        Assert.False(Observe(tracker, 7100.000m, start.AddSeconds(6)));
        Assert.False(Observe(tracker, 10140.000m, start.AddSeconds(12)));
        Assert.False(Observe(tracker, 4880.500m, start.AddSeconds(18)));
        Assert.False(tracker.Recovered);
    }

    [Fact]
    public void MatchToleranceRemainsOneHundredHzAfterNormalization()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        // Dial 4880.600 → center 4882.100, within ±100 Hz of 4882.000.
        Assert.False(Observe(tracker, 4880.600m, start));
        Assert.False(Observe(tracker, 7099.900m, start.AddSeconds(2)));
        Assert.False(Observe(tracker, 10140.100m, start.AddSeconds(4)));
        Assert.True(Observe(tracker, 4880.400m, start.AddSeconds(6)));

        // 200 Hz off dial-equivalent must not match (would require widened tolerance).
        Assert.Null(CatScanMotionTracker.MatchConfiguredCenter(4_880_300, [4_882_000]));
    }

    [Fact]
    public void StaleSamplesAndConfiguredChannelChangesResetState()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        Assert.False(tracker.Observe(4880.500m, start, Channels, start.AddSeconds(16)));
        Assert.False(Observe(tracker, 4880.500m, start.AddSeconds(20)));
        Assert.False(Observe(tracker, 7100.000m, start.AddSeconds(22)));

        tracker.SynchronizeChannels(
        [
            Channels[0],
            new ScanChannel { Number = 4, FrequencyHz = 18_101_500, FrequencyKhz = "18101.500" }
        ]);

        Assert.False(tracker.Recovered);
        Assert.False(tracker.Observe(
            18100.000m,
            start.AddSeconds(24),
            [Channels[0], new ScanChannel { FrequencyHz = 18_101_500 }],
            start.AddSeconds(24)));
    }

    [Fact]
    public void ThreeMatchedObservationsAreInsufficientEvidence()
    {
        var tracker = new CatScanMotionTracker();
        var start = DateTimeOffset.Parse("2026-08-08T12:00:00Z");

        Assert.False(Observe(tracker, 4880.500m, start));
        Assert.False(Observe(tracker, 7100.000m, start.AddSeconds(2)));
        Assert.False(Observe(tracker, 10140.000m, start.AddSeconds(4)));
        Assert.False(tracker.Recovered);
    }

    private static bool Observe(
        CatScanMotionTracker tracker,
        decimal frequencyKhz,
        DateTimeOffset observedAt) =>
        tracker.Observe(frequencyKhz, observedAt, Channels, observedAt);
}
