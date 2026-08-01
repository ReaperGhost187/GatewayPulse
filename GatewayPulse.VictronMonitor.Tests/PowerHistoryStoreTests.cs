using GatewayPulse.PowerMonitoring;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class PowerHistoryStoreTests
{
    [Fact]
    public void RecordAndQuery_PersistsAcrossReloadAndRespectsRange()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gateway-pulse-history-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "PowerHistory.json");
        Directory.CreateDirectory(directory);

        try
        {
            var now = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
            var store = new PowerHistoryStore(path, minSampleInterval: TimeSpan.FromSeconds(1));

            for (var minute = 0; minute < 180; minute++)
            {
                store.Record(new PowerTelemetry
                {
                    UpdatedAt = now.AddMinutes(minute - 179),
                    LastUpdate = now.AddMinutes(minute - 179),
                    Connected = true,
                    StateOfCharge = 50 + minute % 10,
                    Voltage = 13.0m + (minute % 5) * 0.01m,
                    Current = -2.5m,
                    System = new PowerSystemTelemetry
                    {
                        StateOfCharge = 50 + minute % 10,
                        Voltage = 13.0m + (minute % 5) * 0.01m,
                        Current = -2.5m,
                        Watts = -32.5m,
                        TimeRemainingMinutes = 120 + minute
                    }
                }, now.AddMinutes(minute - 179));
            }

            Assert.True(store.Count >= 100);
            Assert.True(File.Exists(path));

            var reloaded = new PowerHistoryStore(path, minSampleInterval: TimeSpan.FromSeconds(1));
            var hour = reloaded.Query(PowerHistoryMetric.StateOfCharge, PowerHistoryRange.OneHour, now);
            var day = reloaded.Query(PowerHistoryMetric.Voltage, PowerHistoryRange.OneDay, now);

            Assert.Equal("soc", hour.Metric);
            Assert.Equal("%", hour.Unit);
            Assert.NotEmpty(hour.Points);
            Assert.All(hour.Points, point => Assert.True(point.Timestamp >= now.AddHours(-1)));
            Assert.True(day.Points.Count >= hour.Points.Count);
            Assert.Equal("V", day.Unit);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Record_IgnoresEmptyTelemetryAndDuplicateBursts()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gateway-pulse-history-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "PowerHistory.json");
        Directory.CreateDirectory(directory);

        try
        {
            var now = new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
            var store = new PowerHistoryStore(path, minSampleInterval: TimeSpan.FromSeconds(30));

            store.Record(new PowerTelemetry { Connected = false }, now);
            Assert.Equal(0, store.Count);

            var sample = new PowerTelemetry
            {
                UpdatedAt = now,
                LastUpdate = now,
                Connected = true,
                System = new PowerSystemTelemetry
                {
                    StateOfCharge = 80,
                    Voltage = 13.5m,
                    Current = 1.2m,
                    Watts = 16.2m
                }
            };

            store.Record(sample, now);
            store.Record(sample, now.AddSeconds(5));
            store.Record(sample, now.AddSeconds(10));
            Assert.Equal(1, store.Count);

            store.Record(sample, now.AddSeconds(35));
            Assert.Equal(1, store.Count); // unchanged values within 5 minutes are skipped
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryParseRange_SupportsExtendedWindowsThrough360Days()
    {
        Assert.True(PowerHistoryStore.TryParseRange("14d", out var fourteen));
        Assert.Equal(PowerHistoryRange.FourteenDays, fourteen);
        Assert.True(PowerHistoryStore.TryParseRange("30", out var thirty));
        Assert.Equal(PowerHistoryRange.ThirtyDays, thirty);
        Assert.True(PowerHistoryStore.TryParseRange("60d", out var sixty));
        Assert.Equal(PowerHistoryRange.SixtyDays, sixty);
        Assert.True(PowerHistoryStore.TryParseRange("90d", out var ninety));
        Assert.Equal(PowerHistoryRange.NinetyDays, ninety);
        Assert.True(PowerHistoryStore.TryParseRange("360d", out var year));
        Assert.Equal(PowerHistoryRange.ThreeHundredSixtyDays, year);
        Assert.Equal("360d", PowerHistoryStore.RangeKey(year));
        Assert.Equal(TimeSpan.FromDays(360), PowerHistoryStore.Retention);
    }
}

