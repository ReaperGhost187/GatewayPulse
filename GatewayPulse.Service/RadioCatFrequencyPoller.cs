using GatewayPulse.Core;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Background poll of CI-V / rigctld so dashboard frequency stays fresh without Trimode probes.
/// </summary>
public sealed class RadioCatFrequencyPoller(
    RadioCatFrequencyClient client,
    RadioCatFrequencyCache cache,
    IOptionsMonitor<GatewayPulseOptions> options,
    ILogger<RadioCatFrequencyPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var cat = options.CurrentValue.RadioCat ?? new RadioCatOptions();
            var delaySeconds = Math.Clamp(cat.PollSeconds > 0 ? cat.PollSeconds : 2, 1, 30);

            if (!cat.Enabled)
            {
                cache.Set(null, "Unknown", "Disabled");
            }
            else
            {
                try
                {
                    var (khz, source, status) = await client.TryGetFrequencyAsync(stoppingToken);
                    cache.Set(khz, source, status);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Radio CAT poll skipped.");
                    cache.SetStatus("Poll error");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
