using GatewayPulse.Core;
using GatewayPulse.RfMonitoring;

namespace GatewayPulse.ServiceHosting;

public sealed class FrequencySnapshotProvider(
    GatewayPulseService gatewayPulse,
    RadioCatFrequencyClient radioCat)
{
    public async Task<FrequencySnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var (catKhz, catUpdated) = await radioCat.TryGetFrequencyAsync(cancellationToken);
        if (catKhz is > 0)
        {
            return FrequencySnapshot.FromObservation(
                catKhz,
                FrequencySources.ManualCat,
                catUpdated ?? now,
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
