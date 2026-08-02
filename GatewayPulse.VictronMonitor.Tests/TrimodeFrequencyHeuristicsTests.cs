using GatewayPulse.Core;
using GatewayPulse.RfMonitoring;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class TrimodeFrequencyHeuristicsTests
{
    [Theory]
    [InlineData(1_800_000, true)]
    [InlineData(7_185_000, true)]
    [InlineData(14_100_000, true)]
    [InlineData(54_000_000, true)]
    [InlineData(1_799_999, false)]
    [InlineData(54_000_001, false)]
    [InlineData(0, false)]
    [InlineData(-7_000_000, false)]
    public void IsPlausibleHfHz_FiltersAmateurHfRange(int hz, bool expected)
    {
        Assert.Equal(expected, TrimodeFrequencyHeuristics.IsPlausibleHfHz(hz));
    }

    [Fact]
    public void LooksLikeFrequencyArrayInRange_RejectsNeighborhoodOfHfValues()
    {
        // Three consecutive HF Int32s: live VFO cells do not sit beside peer table entries.
        var buffer = new byte[16];
        WriteInt32(buffer, 0, 7_100_000);
        WriteInt32(buffer, 4, 7_185_000);
        WriteInt32(buffer, 8, 10_140_000);

        Assert.True(TrimodeFrequencyHeuristics.LooksLikeFrequencyArrayInRange(
            buffer, 4, TrimodeFrequencyHeuristics.HfMinHz, TrimodeFrequencyHeuristics.HfMaxHz));
    }

    [Fact]
    public void LooksLikeFrequencyArrayInRange_AllowsLoneHfValue()
    {
        var buffer = new byte[16];
        WriteInt32(buffer, 0, 12345);
        WriteInt32(buffer, 4, 7_185_000);
        WriteInt32(buffer, 8, 999);

        Assert.False(TrimodeFrequencyHeuristics.LooksLikeFrequencyArrayInRange(
            buffer, 4, TrimodeFrequencyHeuristics.HfMinHz, TrimodeFrequencyHeuristics.HfMaxHz));
    }

    [Fact]
    public void LooksLikeFrequencyArray_DetectsScanListCluster()
    {
        var expected = new HashSet<int> { 7_100_000, 7_185_000, 10_140_000 };
        var buffer = new byte[16];
        WriteInt32(buffer, 0, 7_100_000);
        WriteInt32(buffer, 4, 7_185_000);
        WriteInt32(buffer, 8, 10_140_000);

        Assert.True(TrimodeFrequencyHeuristics.LooksLikeFrequencyArray(buffer, 4, expected));
    }

    [Fact]
    public void ChooseDialCandidate_PrefersPreferredAddress()
    {
        var preferred = new IntPtr(0x2000);
        var candidates = new List<MemoryCandidate>
        {
            new() { Address = new IntPtr(0x1000), Value = 7_100_000 },
            new() { Address = preferred, Value = 14_070_000 },
            new() { Address = new IntPtr(0x3000), Value = 21_070_000 }
        };

        var chosen = TrimodeFrequencyHeuristics.ChooseDialCandidate(candidates, preferred, previousHz: 7_100_000);
        Assert.NotNull(chosen);
        Assert.Equal(preferred, chosen!.Address);
        Assert.Equal(14_070_000, chosen.Value);
    }

    [Fact]
    public void ChooseDialCandidate_PrefersPreviousValueWhenNoPreferredAddress()
    {
        var candidates = new List<MemoryCandidate>
        {
            new() { Address = new IntPtr(0x1000), Value = 7_100_000 },
            new() { Address = new IntPtr(0x2000), Value = 14_070_000 }
        };

        var chosen = TrimodeFrequencyHeuristics.ChooseDialCandidate(candidates, IntPtr.Zero, previousHz: 14_070_000);
        Assert.NotNull(chosen);
        Assert.Equal(14_070_000, chosen!.Value);
    }

    [Fact]
    public void ChooseDialCandidate_ReturnsNullWhenTooAmbiguous()
    {
        var candidates = Enumerable.Range(0, 8)
            .Select(i => new MemoryCandidate
            {
                Address = new IntPtr(0x1000 + i * 4),
                Value = 7_000_000 + i * 25_000
            })
            .ToList();

        var chosen = TrimodeFrequencyHeuristics.ChooseDialCandidate(candidates, IntPtr.Zero, previousHz: null);
        Assert.Null(chosen);
    }

    [Fact]
    public void ChooseDialCandidate_IgnoresArrayMarkedCandidates()
    {
        var candidates = new List<MemoryCandidate>
        {
            new() { Address = new IntPtr(0x1000), Value = 7_100_000, LooksLikeArray = true },
            new() { Address = new IntPtr(0x2000), Value = 14_070_000, LooksLikeArray = false }
        };

        var chosen = TrimodeFrequencyHeuristics.ChooseDialCandidate(candidates, IntPtr.Zero, previousHz: null);
        Assert.NotNull(chosen);
        Assert.Equal(14_070_000, chosen!.Value);
    }

    [Fact]
    public void ChooseDialCandidate_SingleUniqueValueAccepted()
    {
        var candidates = new List<MemoryCandidate>
        {
            new() { Address = new IntPtr(0x1000), Value = 7_185_000 },
            new() { Address = new IntPtr(0x2000), Value = 7_185_000 }
        };

        var chosen = TrimodeFrequencyHeuristics.ChooseDialCandidate(candidates, IntPtr.Zero, previousHz: null);
        Assert.NotNull(chosen);
        Assert.Equal(7_185_000, chosen!.Value);
    }

    [Fact]
    public void FrequencySnapshot_NormalizesTrimodeDialSourceToWinlink()
    {
        var now = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var snap = FrequencySnapshot.FromObservation(7185m, "Trimode dial", now, now);
        Assert.Equal(FrequencySources.Winlink, snap.Source);
        Assert.Equal(FrequencyConfidenceLevels.Live, snap.Confidence);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, 4);
    }
}
