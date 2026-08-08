using GatewayPulse.Core;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Background poll of CI-V / rigctld so dashboard frequency stays fresh without Trimode probes.
/// Never blocks Kestrel: waits for <see cref="IHostApplicationLifetime.ApplicationStarted"/>
/// before any COM/TCP work, and never lets poll exceptions take down the host.
/// </summary>
public sealed class RadioCatFrequencyPoller(
    RadioCatFrequencyClient client,
    RadioCatFrequencyCache cache,
    IOptionsMonitor<GatewayPulseOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<RadioCatFrequencyPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield immediately so BackgroundService.StartAsync returns before any I/O.
        await Task.Yield();

        try
        {
            cache.SetStatus("Waiting for host");
            await WaitForApplicationStartedAsync(stoppingToken);
            if (stoppingToken.IsCancellationRequested)
                return;

            cache.SetStatus("Starting");
            logger.LogInformation("RadioCat frequency poller starting after host listen.");

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
                        if (khz is null or <= 0)
                        {
                            logger.LogWarning(
                                "RadioCat/CI-V poll did not return frequency (mode={Mode}, port={Port}, status={Status}). Service stays up.",
                                cat.Mode,
                                string.IsNullOrWhiteSpace(cat.PortName) ? cat.Host + ":" + cat.Port : cat.PortName,
                                status);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Never fault the hosted service — default host behavior can stop Kestrel.
                        logger.LogWarning(ex, "RadioCat/CI-V poll failed; continuing. Service stays up.");
                        cache.SetStatus("Poll error: " + ex.GetType().Name);
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
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RadioCat frequency poller stopped unexpectedly; HTTP service continues.");
            cache.SetStatus("Poller stopped: " + ex.GetType().Name);
        }
    }

    private async Task WaitForApplicationStartedAsync(CancellationToken stoppingToken)
    {
        if (lifetime.ApplicationStarted.IsCancellationRequested)
            return;

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedReg = lifetime.ApplicationStarted.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            started);
        await using var stopReg = stoppingToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(),
            started);

        if (lifetime.ApplicationStarted.IsCancellationRequested)
            return;

        try
        {
            await started.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down before listen
        }
    }
}
