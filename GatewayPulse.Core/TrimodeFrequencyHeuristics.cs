namespace GatewayPulse.Core;

/// <summary>
/// Pure helpers for Trimode process-memory frequency discovery (scan-list and HF range modes).
/// </summary>
public static class TrimodeFrequencyHeuristics
{
    public const int HfMinHz = 1_800_000;
    public const int HfMaxHz = 54_000_000;
    public const int DefaultMaxRangeCandidates = 40;
    public const int MaxAmbiguousUniqueValues = 5;

    public static bool IsPlausibleHfHz(int hz) =>
        hz >= HfMinHz && hz <= HfMaxHz;

    public static bool IsPlausibleHfHz(int hz, int minHz, int maxHz) =>
        hz >= minHz && hz <= maxHz;

    /// <summary>
    /// True when nearby Int32s are also in the expected scan-channel set (config table).
    /// </summary>
    public static bool LooksLikeFrequencyArray(byte[] buffer, int index, HashSet<int> expectedValues)
    {
        int matchesNearby = 0;

        for (int delta = -32; delta <= 32; delta += 4)
        {
            int pos = index + delta;
            if (pos < 0 || pos + 4 > buffer.Length) continue;

            int value = BitConverter.ToInt32(buffer, pos);
            if (expectedValues.Contains(value))
                matchesNearby++;
        }

        return matchesNearby >= 2;
    }

    /// <summary>
    /// True when nearby Int32s are also plausible HF Hz values (frequency table / band plan).
    /// Self is excluded so a lone live VFO cell is not treated as an array.
    /// </summary>
    public static bool LooksLikeFrequencyArrayInRange(byte[] buffer, int index, int minHz, int maxHz)
    {
        int nearbyInRange = 0;

        for (int delta = -32; delta <= 32; delta += 4)
        {
            if (delta == 0) continue;

            int pos = index + delta;
            if (pos < 0 || pos + 4 > buffer.Length) continue;

            int value = BitConverter.ToInt32(buffer, pos);
            if (IsPlausibleHfHz(value, minHz, maxHz))
                nearbyInRange++;
        }

        return nearbyInRange >= 2;
    }

    /// <summary>
    /// Pick a dial/VFO candidate from a range scrape. Prefer Unknown (null) when ambiguous.
    /// </summary>
    public static MemoryCandidate? ChooseDialCandidate(
        IReadOnlyList<MemoryCandidate> candidates,
        IntPtr preferredAddress,
        int? previousHz)
    {
        if (candidates.Count == 0)
            return null;

        var usable = candidates.Where(c => !c.LooksLikeArray).ToList();
        if (usable.Count == 0)
            return null;

        if (preferredAddress != IntPtr.Zero)
        {
            var preferred = usable.FirstOrDefault(c => c.Address == preferredAddress);
            if (preferred is not null)
                return preferred;
        }

        if (previousHz is int prev && IsPlausibleHfHz(prev))
        {
            var sameValue = usable.FirstOrDefault(c => c.Value == prev);
            if (sameValue is not null)
                return sameValue;
        }

        var uniqueValues = usable.Select(c => c.Value).Distinct().Count();
        if (uniqueValues > MaxAmbiguousUniqueValues)
            return null;

        if (uniqueValues == 1)
            return usable[0];

        // A small handful of distinct values is still usable when the scrape is otherwise clean.
        if (usable.Count <= 3)
            return usable[0];

        return null;
    }
}
