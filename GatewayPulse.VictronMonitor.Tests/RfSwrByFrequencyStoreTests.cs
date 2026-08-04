using System.Text.Json;
using GatewayPulse.RfMonitoring;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class RfSwrByFrequencyStoreTests
{
    [Fact]
    public void TryAddFromSession_RequiresValidFrequencyAndSwr()
    {
        var path = Path.Combine(Path.GetTempPath(), "gp-swr-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new RfSwrByFrequencyStore(path);
            Assert.False(store.TryAddFromSession(new RfTransmissionEvent
            {
                PeakForwardPowerWatts = 50,
                MaxSwr = 1.2m,
                StartFrequencyKhz = null
            }));
            Assert.False(store.TryAddFromSession(new RfTransmissionEvent
            {
                PeakForwardPowerWatts = 50,
                StartFrequencyKhz = 7100m,
                MaxSwr = null,
                AverageSwr = null
            }));
            Assert.True(store.TryAddFromSession(new RfTransmissionEvent
            {
                Id = "abc",
                StartTime = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
                EndTime = DateTimeOffset.Parse("2026-08-01T00:01:00Z"),
                DurationSeconds = 60,
                PeakForwardPowerWatts = 80,
                MaxReflectedPowerWatts = 2,
                MaxSwr = 1.35m,
                AverageSwr = 1.20m,
                StartFrequencyKhz = 7100.0m,
                FrequencySource = FrequencySources.Winlink,
                FrequencyConfidence = FrequencyConfidenceLevels.Live,
                BurstCount = 3
            }));

            var json = JsonSerializer.Serialize(store.Query(range: "all"));
            Assert.Contains("7100000", json);
            Assert.Contains("1.35", json);
            Assert.Contains("Winlink", json);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Query_AggregateOrdersByFrequencyAndReportsSampleCount()
    {
        var path = Path.Combine(Path.GetTempPath(), "gp-swr-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new RfSwrByFrequencyStore(path);
            var t0 = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
            store.Add(Obs(t0, 7_100_000, 1.2m, 1.1m));
            store.Add(Obs(t0.AddMinutes(1), 7_100_050, 1.4m, 1.3m)); // same 100 Hz bucket
            store.Add(Obs(t0.AddMinutes(2), 14_070_000, 1.5m, 1.4m));

            // Fresh store instance proves persistence retained all three observations.
            var reloaded = new RfSwrByFrequencyStore(path);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
                reloaded.Query(
                    range: "custom",
                    from: t0.AddHours(-1),
                    to: t0.AddHours(1),
                    aggregate: true,
                    bucketHz: 100)));
            var root = doc.RootElement;
            Assert.Equal(3, root.GetProperty("observationCount").GetInt32());
            var aggregates = root.GetProperty("aggregates");
            Assert.Equal(2, aggregates.GetArrayLength());
            Assert.Equal(2, aggregates[0].GetProperty("sampleCount").GetInt32());
            Assert.True(aggregates[0].GetProperty("frequencyHz").GetInt64() < 14_000_000);
            Assert.True(aggregates[1].GetProperty("frequencyHz").GetInt64() > aggregates[0].GetProperty("frequencyHz").GetInt64());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Query_CompareMarksGettingWorse()
    {
        var path = Path.Combine(Path.GetTempPath(), "gp-swr-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new RfSwrByFrequencyStore(path);
            var now = DateTimeOffset.UtcNow;
            store.Add(Obs(now.AddDays(-3), 7_100_000, 1.6m, 1.5m)); // current 7d
            store.Add(Obs(now.AddDays(-10), 7_100_000, 1.2m, 1.1m)); // previous 7d

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(store.Query(range: "30d", compare: "7d")));
            var buckets = doc.RootElement.GetProperty("comparison").GetProperty("buckets");
            Assert.True(buckets.GetArrayLength() >= 1);
            Assert.True(buckets[0].GetProperty("gettingWorse").GetBoolean());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Query_FiltersStaleConfidenceWhenLiveRecentSelected()
    {
        var path = Path.Combine(Path.GetTempPath(), "gp-swr-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new RfSwrByFrequencyStore(path);
            var now = DateTimeOffset.UtcNow;
            var live = Obs(now, 7_100_000, 1.2m, 1.1m);
            live.FrequencyConfidence = FrequencyConfidenceLevels.Live;
            var stale = Obs(now, 7_200_000, 1.3m, 1.2m);
            stale.FrequencyConfidence = FrequencyConfidenceLevels.Stale;
            store.Add(live);
            store.Add(stale);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
                store.Query(range: "24h", confidence: "live_recent")));
            Assert.Equal(1, doc.RootElement.GetProperty("observationCount").GetInt32());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static RfSwrByFrequencyObservation Obs(
        DateTimeOffset timestamp,
        long frequencyHz,
        decimal maxSwr,
        decimal avgSwr) => new()
    {
        Timestamp = timestamp,
        FrequencyHz = frequencyHz,
        MaxSwr = maxSwr,
        AverageSwr = avgSwr,
        PeakForwardPowerWatts = 50,
        MaxReflectedPowerWatts = 1,
        ReflectedPowerSource = RfReflectedPowerSources.Calculated,
        DurationSeconds = 12,
        FrequencySource = FrequencySources.Winlink,
        FrequencyConfidence = FrequencyConfidenceLevels.Live
    };
}
