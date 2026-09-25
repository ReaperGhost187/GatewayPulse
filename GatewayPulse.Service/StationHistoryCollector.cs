using GatewayPulse.Core;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Indexes RMS Relay logs into the station contact archive on a schedule, so contacts are
/// archived before RMS Relay prunes its logs even if no one opens Station History.
/// </summary>
public sealed class StationHistoryCollector : BackgroundService
{
    public const int DefaultIntervalMinutes = 5;

    private readonly StationHistoryStore _store;
    private readonly ILogger<StationHistoryCollector> _logger;
    private readonly TimeSpan _interval;

    public StationHistoryCollector(
        StationHistoryStore store,
        ILogger<StationHistoryCollector> logger,
        TimeSpan? interval = null)
    {
        _store = store;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromMinutes(DefaultIntervalMinutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Log parsing is synchronous file I/O; keep it off the host's startup path.
                await Task.Run(() => _store.Refresh(force: true), stoppingToken);
                var coverage = _store.GetCoverage();
                _logger.LogDebug(
                    "Station history indexed: {Archived} contacts, {Files} Relay log files, oldest {Oldest}.",
                    coverage.ArchivedContacts,
                    coverage.LogFilesScanned,
                    coverage.OldestContact);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Station history indexing failed; retrying next interval.");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
