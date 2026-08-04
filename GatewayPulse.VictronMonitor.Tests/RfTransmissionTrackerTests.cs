using GatewayPulse.RfMonitoring;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class RfTransmissionTrackerTests
{
    [Fact]
    public void Process_CreatesEventOnThresholdAndCompletesAfterCoalesce()
    {
        var tracker = new RfTransmissionTracker(0.05m, TimeSpan.FromMilliseconds(200));
        var t0 = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var live = FrequencySnapshot.FromObservation(7185.0m, "Winlink", t0, t0);

        Assert.Null(tracker.Process(0m, 0m, null, live, t0));
        Assert.Null(tracker.Process(80m, 2m, 1.2m, live, t0.AddMilliseconds(100)));
        Assert.NotNull(tracker.Active);
        Assert.Null(tracker.Process(90m, 3m, 1.3m, live, t0.AddMilliseconds(300)));
        Assert.Null(tracker.Process(0m, 0m, null, live, t0.AddMilliseconds(400)));
        Assert.Null(tracker.Process(0m, 0m, null, live, t0.AddMilliseconds(500)));
        var completed = tracker.Process(0m, 0m, null, live, t0.AddMilliseconds(650));
        Assert.NotNull(completed);
        Assert.False(completed!.InProgress);
        Assert.Equal(1, completed.BurstCount);
        Assert.Equal(90m, completed.PeakForwardPowerWatts);
        Assert.Equal(3m, completed.MaxReflectedPowerWatts);
        Assert.Equal(RfReflectedPowerSources.Calculated, completed.MaxReflectedPowerSource);
        Assert.Equal(1.3m, completed.MaxSwr);
        Assert.Equal(7185.0m, completed.StartFrequencyKhz);
        Assert.Equal(FrequencySources.Winlink, completed.FrequencySource);
        Assert.Equal(FrequencyConfidenceLevels.Live, completed.FrequencyConfidence);
    }

    [Fact]
    public void Process_CoalescesPactorBurstsIntoOneSession()
    {
        var tracker = new RfTransmissionTracker(0.05m, TimeSpan.FromMilliseconds(6000));
        var t0 = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var live = FrequencySnapshot.FromObservation(7100m, "Winlink", t0, t0);

        Assert.Null(tracker.Process(50m, 1m, 1.2m, live, t0));
        Assert.Null(tracker.Process(0m, 0m, null, live, t0.AddSeconds(1)));
        // Gap < 6000 ms — still same session
        Assert.Null(tracker.Process(60m, 2m, 1.4m, live, t0.AddSeconds(3)));
        Assert.NotNull(tracker.Active);
        Assert.Equal(2, tracker.Active!.BurstCount);
        Assert.Equal(60m, tracker.Active.PeakForwardPowerWatts);
        Assert.Equal(1.4m, tracker.Active.MaxSwr);

        Assert.Null(tracker.Process(0m, 0m, null, live, t0.AddSeconds(4)));
        Assert.Null(tracker.Process(0m, 0m, null, live, t0.AddSeconds(9))); // 5s quiet — still coalescing
        var completed = tracker.Process(0m, 0m, null, live, t0.AddSeconds(10.1)); // >6s after last drop
        Assert.NotNull(completed);
        Assert.Equal(2, completed!.BurstCount);
        Assert.Equal(60m, completed.PeakForwardPowerWatts);
        Assert.Equal(1.4m, completed.MaxSwr);
        Assert.True(completed.DurationSeconds >= 10);
    }

    [Fact]
    public void Process_IgnoresSwrBelowMinForward()
    {
        var tracker = new RfTransmissionTracker(
            0.05m,
            TimeSpan.FromMilliseconds(200),
            swrMinForwardWatts: 0.5m);
        var t0 = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var live = FrequencySnapshot.FromObservation(7100m, "Winlink", t0, t0);

        tracker.Process(0.2m, 0.1m, 3.5m, live, t0); // above TX threshold but below SWR min
        Assert.NotNull(tracker.Active);
        Assert.Null(tracker.Active!.MaxSwr);

        tracker.Process(10m, 0.5m, 1.25m, live, t0.AddMilliseconds(50));
        Assert.Equal(1.25m, tracker.Active.MaxSwr);

        tracker.Process(0m, 0m, null, live, t0.AddMilliseconds(100));
        var completed = tracker.Process(0m, 0m, null, live, t0.AddMilliseconds(350));
        Assert.NotNull(completed);
        Assert.Equal(1.25m, completed!.MaxSwr);
        Assert.Equal(10m, completed.PeakForwardPowerWatts);
    }

    [Fact]
    public void FormatSwrDisplay_ShowsResolutionFloor()
    {
        Assert.Equal("≤1.00", RfTransmissionTracker.FormatSwrDisplay(1.00m, true));
        Assert.Equal("≤1.00", RfTransmissionTracker.FormatSwrDisplay(1.00m));
        Assert.Equal("1.25", RfTransmissionTracker.FormatSwrDisplay(1.25m));
        Assert.Equal("—", RfTransmissionTracker.FormatSwrDisplay(null));
    }

    [Fact]
    public void Process_FlagsFrequencyChangeDuringTx()
    {
        var tracker = new RfTransmissionTracker(0.05m, TimeSpan.FromMilliseconds(100));
        var t0 = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var start = FrequencySnapshot.FromObservation(7100m, "Manual/CAT", t0, t0);
        var end = FrequencySnapshot.FromObservation(7185m, "Manual/CAT", t0.AddSeconds(1), t0.AddSeconds(1));

        tracker.Process(50m, 1m, 1.1m, start, t0);
        tracker.Process(55m, 1m, 1.1m, end, t0.AddMilliseconds(200));
        tracker.Process(0m, 0m, null, end, t0.AddMilliseconds(250));
        var completed = tracker.Process(0m, 0m, null, end, t0.AddMilliseconds(400));
        Assert.NotNull(completed);
        Assert.True(completed!.FrequencyChangedDuringTx);
        Assert.Equal(7100m, completed.StartFrequencyKhz);
        Assert.Equal(7185m, completed.EndFrequencyKhz);
        Assert.Contains("Frequency changed during TX", completed.FrequencyNote);
    }

    [Fact]
    public void FrequencySnapshot_ClassifiesConfidenceByAge()
    {
        var now = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        Assert.Equal(FrequencyConfidenceLevels.Live,
            FrequencySnapshot.FromObservation(7m, "Winlink", now.AddSeconds(-1), now).Confidence);
        Assert.Equal(FrequencyConfidenceLevels.Recent,
            FrequencySnapshot.FromObservation(7m, "Winlink", now.AddSeconds(-10), now).Confidence);
        Assert.Equal(FrequencyConfidenceLevels.Stale,
            FrequencySnapshot.FromObservation(7m, "Winlink", now.AddSeconds(-42), now).Confidence);
        Assert.Equal(FrequencyConfidenceLevels.Unknown, FrequencySnapshot.Unknown().Confidence);
        Assert.Equal(FrequencyConfidenceLevels.Stale,
            FrequencySnapshot.FromObservation(7m, "Winlink", null, now).Confidence);
    }
}
