using GatewayPulse.Core;
using GatewayPulse.RfMonitoring;

namespace GatewayPulse.ServiceHosting;

public sealed class FrequencySnapshotProvider(
    GatewayPulseService gatewayPulse,
    RadioCatFrequencyCache radioCatCache,
    RadioCatFrequencyClient radioCat)
{
    public async Task<FrequencySnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Prefer the background cache (avoids opening the CI-V port on every TX sample).
        var (cachedKhz, cachedSource, cachedUpdated, _) = radioCatCache.Snapshot();
        if (cachedKhz is > 0)
        {
            return FrequencySnapshot.FromObservation(
                cachedKhz,
                cachedSource.Contains("CI-V", StringComparison.OrdinalIgnoreCase)
                    ? FrequencySources.ManualCat
                    : FrequencySources.ManualCat,
                cachedUpdated ?? now,
                now);
        }

        var (catKhz, catSource, _) = await radioCat.TryGetFrequencyAsync(cancellationToken);
        if (catKhz is > 0)
        {
            return FrequencySnapshot.FromObservation(
                catKhz,
                string.IsNullOrWhiteSpace(catSource) ? FrequencySources.ManualCat : catSource,
                now,
                now);
        }

        var (winlinkKhz, winlinkSource, winlinkUpdated) = gatewayPulse.GetWinlinkFrequencyObservation();
        if (winlinkKhz is > 0)
        {
            return FrequencySnapshot.FromObservation(
                winlinkKhz,
                winlinkSource,
                winlinkUpdated,
                now);
        }

        return FrequencySnapshot.Unknown();
    }
}
