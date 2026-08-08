using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace GatewayPulse.Core;

public sealed class GatewayPulseService
{
    private readonly IOptionsMonitor<GatewayPulseOptions> _options;
    private readonly IOptionsMonitor<AlertOptions> _alerts;
    private readonly PushoverService _pushover;
    private readonly object _lock = new();

    private GatewayStatus _status = new();
    private string _lastAlertStateKey = "";
    private string _lastStationAlertKey = "";
    private bool _stationAlertPrimed;
    private DateTime _lastAlertSentUtc = DateTime.MinValue;
    private DateTime _lastStationAlertSentUtc = DateTime.MinValue;
    private IntPtr _cachedFrequencyAddress = IntPtr.Zero;
    private IntPtr _pendingDialAddress = IntPtr.Zero;
    private int _pendingDialValue;
    private int _pendingDialConfirmations;
    private int _frequencyMissStreak;
    private decimal? _observedFrequencyKhz;
    private string _observedFrequencySource = "Unknown";
    private DateTimeOffset? _observedFrequencyUpdatedAt;
    private int _lastScanMemoryValue;
    private int _scanStagnantPolls;
    private DateTime _lastFullScanSearchUtc = DateTime.MinValue;
    private readonly List<IntPtr> _scanProbeAddresses = new();
    private readonly Dictionary<long, int> _scanProbeLastValues = new();
    private bool? _lastKnownScannerEnabled;
    private DateTime _lastScannerOkUtc = DateTime.MinValue;
    private DateTime _lastScannerAttemptUtc = DateTime.MinValue;
    private DateTime _lastIniParseUtc = DateTime.MinValue;

    /// <summary>Consecutive memory-read misses before clearing TX observation (avoids flicker).</summary>
    private const int FrequencyMissGracePolls = 2;
    private const int ScanStagnantPollsBeforeRescan = 3;
    /// <summary>Full Trimode VA walks are expensive — never more often than this.</summary>
    private const int FullScanSearchMinIntervalMs = 10_000;
    private const int ScannerStaleGraceSeconds = 30;
    /// <summary>TCP SCAN is read-only but opening :8510 too often can upset Trimode.</summary>
    private const int ScannerPollMinIntervalSeconds = 15;
    private const int IniParseMinIntervalSeconds = 60;

    public GatewayPulseService(
        IOptionsMonitor<GatewayPulseOptions> options,
        IOptionsMonitor<AlertOptions> alerts,
        PushoverService pushover)
    {
        _options = options;
        _alerts = alerts;
        _pushover = pushover;
        // Warm caches, but never let a log/INI parse failure kill DI / host start.
        try { _ = GetStatus(); }
        catch { /* first request will retry */ }
    }

    public GatewayStatus GetStatus()
    {
        // Log parsing is expensive — keep it outside the lock so the live-radio poller
        // can keep tracking Trimode scan hops while /api/status is building.
        var status = BuildStatusFromLogs();

        lock (_lock)
        {
            // Never start/stop Trimode. Probes are opt-in — off by default (can recycle Trimode).
            var probe = _options.CurrentValue.TrimodeProbe ?? new TrimodeProbeOptions();
            if (probe.CommandPortEnabled)
                PollTrimodeScannerStatus(status, force: true, allowRetry: false);
            else
                ApplyProbeDisabledScannerStatus(status);

            if (probe.MemoryReadEnabled)
                TryReadTrimodeMemory(status, allowFullMemorySearch: true);
            else
                ApplyProbeDisabledFrequencyStatus(status);

            status.Healthy =
                status.RelayRunning == true &&
                status.TrimodeSeen &&
                status.ScannerEnabled != false;

            EvaluateAlerts(status);
            EvaluateStationAlert(status);
            _status = status;
            return status;
        }
    }

    /// <summary>
    /// Best Winlink/Trimode frequency observation for RF TX logging (not CAT).
    /// Only returns live Trimode memory/dial observations — never the INI channel-1 seed.
    /// </summary>
    public (decimal? FrequencyKhz, string Source, DateTimeOffset? UpdatedAt) GetWinlinkFrequencyObservation()
    {
        lock (_lock)
        {
            // Prefer the latest observation without a full Refresh to keep TX sampling light.
            if (_observedFrequencyKhz is > 0)
                return (_observedFrequencyKhz, _observedFrequencySource, _observedFrequencyUpdatedAt);

            // Do not fall back to ParseTrimodeIni channel-1 ("Configured") — that mis-tags TX history.
            return (null, "Unknown", null);
        }
    }

    /// <summary>Last known station without running a full log/status refresh.</summary>
    public string? PeekLastStation()
    {
        lock (_lock)
            return string.IsNullOrWhiteSpace(_status.LastStation) ? null : _status.LastStation;
    }

    /// <summary>
    /// Live-radio poller entry point. No-op unless TrimodeProbe is explicitly enabled.
    /// </summary>
    public GatewayStatus RefreshLiveRadioState()
    {
        lock (_lock)
        {
            var probe = _options.CurrentValue.TrimodeProbe ?? new TrimodeProbeOptions();
            if (!probe.CommandPortEnabled && !probe.MemoryReadEnabled)
            {
                _status.TrimodeSeen = IsProcessRunning("RMS Trimode");
                ApplyProbeDisabledScannerStatus(_status);
                ApplyProbeDisabledFrequencyStatus(_status);
                _status.Healthy =
                    _status.RelayRunning == true &&
                    _status.TrimodeSeen &&
                    _status.ScannerEnabled != false;
                return SnapshotLiveRadio(_status);
            }

            var now = DateTime.UtcNow;
            var live = new GatewayStatus
            {
                GatewayName = _status.GatewayName,
                Callsign = _status.Callsign,
                DemoMode = _status.DemoMode,
                TrimodeSeen = IsProcessRunning("RMS Trimode")
            };

            if (_status.ScanChannels.Count > 0 &&
                _lastIniParseUtc != DateTime.MinValue &&
                (now - _lastIniParseUtc).TotalSeconds < IniParseMinIntervalSeconds)
            {
                live.ScanChannels = _status.ScanChannels
                    .Select(c => new ScanChannel
                    {
                        Number = c.Number,
                        FrequencyKhz = c.FrequencyKhz,
                        FrequencyHz = c.FrequencyHz,
                        Mode = c.Mode,
                        Active = c.Active,
                        ServiceCode = c.ServiceCode
                    })
                    .ToList();
            }
            else
            {
                ParseTrimodeIni(live);
                _lastIniParseUtc = now;
            }

            if (probe.CommandPortEnabled)
                PollTrimodeScannerStatus(live, force: false, allowRetry: false);
            else
                ApplyProbeDisabledScannerStatus(live);

            if (probe.MemoryReadEnabled)
                TryReadTrimodeMemory(live, allowFullMemorySearch: false);
            else
                ApplyProbeDisabledFrequencyStatus(live);

            _status.TrimodeSeen = live.TrimodeSeen;
            _status.ScannerEnabled = live.ScannerEnabled;
            _status.ScannerStatus = live.ScannerStatus;
            _status.CommandPortStatus = live.CommandPortStatus;
            _status.MemoryReadStatus = live.MemoryReadStatus;
            _status.MemoryAddress = live.MemoryAddress;
            _status.CurrentFrequencyKhz = live.CurrentFrequencyKhz;
            _status.DialFrequencyKhz = live.DialFrequencyKhz;
            _status.LiveFrequencySource = live.LiveFrequencySource;
            _status.FrequencyUpdatedAt = live.FrequencyUpdatedAt;
            _status.ScanChannels = live.ScanChannels;
            _status.Healthy =
                _status.RelayRunning == true &&
                _status.TrimodeSeen &&
                _status.ScannerEnabled != false;

            return SnapshotLiveRadio(_status);
        }
    }

    private void ApplyProbeDisabledScannerStatus(GatewayStatus status)
    {
        status.CommandPortStatus = "Disabled (TrimodeProbe.CommandPortEnabled=false)";

        // Trimode SCAN probe is off — do not imply a Trimode scanner fault.
        // When RadioCat/CI-V is the live-frequency path, report that calmly.
        var radioCat = _options.CurrentValue.RadioCat;
        if (radioCat?.Enabled == true)
        {
            status.ScannerEnabled = true;
            var mode = radioCat.Mode ?? "";
            status.ScannerStatus = mode.Equals("Rigctld", StringComparison.OrdinalIgnoreCase)
                ? "Via CAT"
                : "Via CI-V";
            return;
        }

        status.ScannerEnabled = null;
        status.ScannerStatus = "Not probed";
    }

    private void ApplyProbeDisabledFrequencyStatus(GatewayStatus status)
    {
        status.MemoryReadStatus = "Disabled (TrimodeProbe.MemoryReadEnabled=false)";
        status.MemoryAddress = "";
        status.CurrentFrequencyKhz = "--";
        status.DialFrequencyKhz = "--";
        status.LiveFrequencySource = "Configured";
        status.FrequencyUpdatedAt = null;
        ClearFrequencyObservation();
    }

    public GatewayStatus GetLiveRadioSnapshot()
    {
        lock (_lock)
            return SnapshotLiveRadio(_status);
    }

    private static GatewayStatus SnapshotLiveRadio(GatewayStatus status) => new()
    {
        TrimodeSeen = status.TrimodeSeen,
        ScannerEnabled = status.ScannerEnabled,
        ScannerStatus = status.ScannerStatus,
        CommandPortStatus = status.CommandPortStatus,
        MemoryReadStatus = status.MemoryReadStatus,
        MemoryAddress = status.MemoryAddress,
        CurrentFrequencyKhz = status.CurrentFrequencyKhz,
        DialFrequencyKhz = status.DialFrequencyKhz,
        LiveFrequencySource = status.LiveFrequencySource,
        FrequencyUpdatedAt = status.FrequencyUpdatedAt,
        ScanChannels = status.ScanChannels,
        Healthy = status.Healthy,
        LastScan = status.LastScan
    };

    private GatewayStatus BuildStatusFromLogs()
    {
        var options = _options.CurrentValue;

        var status = new GatewayStatus
        {
            GatewayName = options.GatewayName,
            Callsign = options.Callsign,
            LastScan = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };

        var eventsList = new List<GatewayEvent>();
        var stationCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stationConnections = new List<StationConnection>();
        var hourlyActivity = CreateHourlyActivity();

        status.RelayRunning = IsProcessRunning("RMS Relay");
        status.TrimodeSeen = IsProcessRunning("RMS Trimode");

        ParseRelayLogs(status, eventsList, stationCounts, stationConnections, hourlyActivity);
        ParseTrimodeLogs(status, eventsList, hourlyActivity);
        ParseTrimodeIni(status);
        ApplyProcessStartTimes(status);

        status.StationCounts = stationCounts
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new { station = kv.Key, count = kv.Value })
            .Cast<object>()
            .ToList();

        status.RecentStationConnections = stationConnections
            .OrderByDescending(c => ParseAnyTime(c.Timestamp) ?? DateTime.MinValue)
            .Take(50)
            .ToList();

        status.HourlyActivity = hourlyActivity;
        status.UptimeMetrics = CreateUptimeMetrics(status);

        status.RecentEvents = eventsList
            .DistinctBy(e => $"{e.Timestamp}|{e.Source}|{e.Type}|{e.Detail}")
            .OrderByDescending(e => ParseAnyTime(e.Timestamp) ?? DateTime.MinValue)
            .Take(80)
            .ToList();

        return status;
    }

    private void EvaluateAlerts(GatewayStatus status)
    {
        var alerts = _alerts.CurrentValue;
        var problems = new List<string>();

        if (alerts.RelayOffline && status.RelayRunning != true)
            problems.Add("RMS Relay is offline");

        if (alerts.TrimodeOffline && !status.TrimodeSeen)
            problems.Add("RMS Trimode is offline");

        if (alerts.ScannerStopped && status.TrimodeSeen && status.ScannerEnabled == false)
            problems.Add("Scanner is stopped");

        var currentStateKey = problems.Count == 0
            ? "HEALTHY"
            : string.Join("|", problems);

        if (currentStateKey == _lastAlertStateKey)
            return;

        var now = DateTime.UtcNow;

        if (_lastAlertSentUtc != DateTime.MinValue &&
            (now - _lastAlertSentUtc).TotalMinutes < _pushover.CooldownMinutes)
        {
            _lastAlertStateKey = currentStateKey;
            return;
        }

        _lastAlertStateKey = currentStateKey;

        if (problems.Count > 0)
        {
            _lastAlertSentUtc = now;

            _ = _pushover.SendAsync(
                "🔴 Gateway Pulse Alert",
                $"{status.GatewayName}\n\n{string.Join("\n", problems)}\n\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        else if (alerts.Recovery)
        {
            _lastAlertSentUtc = now;

            _ = _pushover.SendAsync(
                "🟢 Gateway Pulse Recovery",
                $"{status.GatewayName}\n\nGateway health is restored.\n\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
    }

    private void EvaluateStationAlert(GatewayStatus status)
    {
        var alerts = _alerts.CurrentValue;

        if (!alerts.StationConnected || string.IsNullOrWhiteSpace(status.LastStation))
            return;

        var stationKey = $"{status.LastStation}|{status.LastRelayEvent}";
        if (stationKey == _lastStationAlertKey)
            return;

        _lastStationAlertKey = stationKey;

        if (!_stationAlertPrimed)
        {
            _stationAlertPrimed = true;
            return;
        }

        var now = DateTime.UtcNow;
        if (_lastStationAlertSentUtc != DateTime.MinValue &&
            (now - _lastStationAlertSentUtc).TotalMinutes < _pushover.CooldownMinutes)
        {
            return;
        }

        _lastStationAlertSentUtc = now;

        _ = _pushover.SendAsync(
            "Gateway Pulse Station Connected",
            $"{status.GatewayName}\n\nStation connected: {status.LastStation}\n\n{status.LastRelayEvent}");
    }

    private void TryReadTrimodeMemory(GatewayStatus status, bool allowFullMemorySearch = true)
    {
        if (!status.TrimodeSeen)
        {
            status.MemoryReadStatus = "Trimode offline";
            ClearFrequencyObservation();
            return;
        }

        var expected = status.ScanChannels
            .Where(c => c.FrequencyHz > 0)
            .Select(c => c.FrequencyHz)
            .Distinct()
            .ToHashSet();

        var scannerStopped = status.ScannerEnabled == false;
        var scannerScanning = status.ScannerEnabled == true;

        // Scan-list match while scanning or when scanner state is unknown (safe default).
        // When stopped, allow dial range scrape even if the INI scan list is empty.
        if (!scannerStopped && expected.Count == 0)
        {
            status.MemoryReadStatus = "No configured frequencies to match";
            NoteFrequencyMiss(status, "No configured frequencies to match");
            return;
        }

        try
        {
            var proc = Process.GetProcesses()
                .FirstOrDefault(p =>
                    p.ProcessName.Equals("RMS Trimode", StringComparison.OrdinalIgnoreCase) ||
                    p.ProcessName.Contains("Trimode", StringComparison.OrdinalIgnoreCase));

            if (proc is null)
            {
                NoteFrequencyMiss(status, "Trimode process not found", clearCache: true);
                return;
            }

            using var reader = new ProcessMemoryReader(proc.Id);

            if (scannerStopped)
            {
                // Dial range scrape walks a lot of memory — only on full status refreshes.
                if (!allowFullMemorySearch)
                {
                    if (_cachedFrequencyAddress != IntPtr.Zero &&
                        reader.TryReadInt32(_cachedFrequencyAddress, out var dialCached) &&
                        TrimodeFrequencyHeuristics.IsPlausibleHfHz(dialCached))
                    {
                        ApplyLiveFrequency(status, dialCached, "Trimode dial", _cachedFrequencyAddress);
                        status.MemoryReadStatus = "OK cached dial";
                        return;
                    }

                    status.MemoryReadStatus = "Dial rediscovery deferred (live poll)";
                    return;
                }

                TryReadTrimodeDialFrequency(status, reader, expected);
                return;
            }

            // Scanning or unknown: scan-list Int32 match only (never trust LooksLikeArray config rows).
            ResetPendingDialCandidate();
            TryReadTrimodeScanFrequency(status, reader, expected, scannerScanning, allowFullMemorySearch);
        }
        catch (Exception ex)
        {
            NoteFrequencyMiss(status, "Memory read error: " + ex.GetType().Name, clearCache: true);
        }
    }

    private void TryReadTrimodeScanFrequency(
        GatewayStatus status,
        ProcessMemoryReader reader,
        HashSet<int> expected,
        bool scannerScanning,
        bool allowFullMemorySearch)
    {
        // Cheap path: re-read known probe addresses; prefer any cell that changed since last poll.
        if (_scanProbeAddresses.Count > 0)
        {
            var previousByAddress = new Dictionary<long, int>(_scanProbeLastValues);
            var probeCandidates = new List<MemoryCandidate>();
            foreach (var addr in _scanProbeAddresses)
            {
                if (!reader.TryReadInt32(addr, out var value) || !expected.Contains(value))
                    continue;
                probeCandidates.Add(new MemoryCandidate { Address = addr, Value = value });
            }

            var hopped = TrimodeFrequencyHeuristics.ChooseScanningCandidate(
                probeCandidates,
                preferredAddress: _cachedFrequencyAddress,
                excludeAddress: IntPtr.Zero,
                previousHz: _lastScanMemoryValue > 0 ? _lastScanMemoryValue : null,
                previousByAddress: previousByAddress);

            foreach (var c in probeCandidates)
                _scanProbeLastValues[c.Address.ToInt64()] = c.Value;

            if (hopped is not null &&
                previousByAddress.TryGetValue(hopped.Address.ToInt64(), out var was) &&
                was != hopped.Value)
            {
                AcceptScanFrequency(status, hopped, "OK live (changing)");
                return;
            }
        }

        // Cached address still holds a scan-list value.
        if (_cachedFrequencyAddress != IntPtr.Zero &&
            reader.TryReadInt32(_cachedFrequencyAddress, out var cachedValue) &&
            expected.Contains(cachedValue))
        {
            if (!scannerScanning || cachedValue != _lastScanMemoryValue)
            {
                _scanStagnantPolls = 0;
                AcceptScanFrequency(status, new MemoryCandidate
                {
                    Address = _cachedFrequencyAddress,
                    Value = cachedValue
                }, scannerScanning ? "OK cached (scan)" : "OK cached");
                return;
            }

            _scanStagnantPolls++;
            if (_scanStagnantPolls < ScanStagnantPollsBeforeRescan)
            {
                AcceptScanFrequency(status, new MemoryCandidate
                {
                    Address = _cachedFrequencyAddress,
                    Value = cachedValue
                }, "OK cached (scan)");
                return;
            }
            // Stagnant while scanning → likely a static scan-list table cell; rediscover.
        }

        var now = DateTime.UtcNow;
        var searchDue = allowFullMemorySearch &&
                        (now - _lastFullScanSearchUtc).TotalMilliseconds >= FullScanSearchMinIntervalMs;

        if (!searchDue)
        {
            if (_cachedFrequencyAddress != IntPtr.Zero &&
                reader.TryReadInt32(_cachedFrequencyAddress, out var holdValue) &&
                expected.Contains(holdValue))
            {
                ApplyLiveFrequency(status, holdValue, "Trimode memory", _cachedFrequencyAddress);
                status.MemoryReadStatus = allowFullMemorySearch
                    ? "OK cached (scan, awaiting rediscovery)"
                    : "OK cached (scan)";
                return;
            }

            status.MemoryReadStatus = allowFullMemorySearch
                ? "Scan rediscovery rate-limited"
                : "Scan rediscovery deferred (live poll)";
            return;
        }

        _lastFullScanSearchUtc = now;
        var exclude = _scanStagnantPolls >= ScanStagnantPollsBeforeRescan
            ? _cachedFrequencyAddress
            : IntPtr.Zero;

        var priorProbeValues = new Dictionary<long, int>(_scanProbeLastValues);
        var candidates = reader.FindInt32Candidates(expected, maxCandidates: 60);
        var usable = candidates.Where(c => !c.LooksLikeArray).ToList();

        _scanProbeAddresses.Clear();
        foreach (var c in usable.Take(40))
            _scanProbeAddresses.Add(c.Address);

        foreach (var c in usable)
            _scanProbeLastValues[c.Address.ToInt64()] = c.Value;

        var chosen = TrimodeFrequencyHeuristics.ChooseScanningCandidate(
            usable,
            preferredAddress: IntPtr.Zero,
            excludeAddress: exclude,
            previousHz: _lastScanMemoryValue > 0 ? _lastScanMemoryValue : null,
            previousByAddress: priorProbeValues);

        if (chosen is null)
        {
            NoteFrequencyMiss(status, usable.Count == 0
                ? "No frequency candidate found"
                : "No usable scan frequency candidate");
            return;
        }

        var changed = priorProbeValues.TryGetValue(chosen.Address.ToInt64(), out var prev) &&
                      prev != chosen.Value;
        AcceptScanFrequency(
            status,
            chosen,
            changed ? "OK live (changing)" : $"OK candidate found ({usable.Count})");
    }

    private void AcceptScanFrequency(GatewayStatus status, MemoryCandidate chosen, string memoryStatus)
    {
        _cachedFrequencyAddress = chosen.Address;
        if (chosen.Value != _lastScanMemoryValue)
            _scanStagnantPolls = 0;
        _lastScanMemoryValue = chosen.Value;
        _scanProbeLastValues[chosen.Address.ToInt64()] = chosen.Value;
        ApplyLiveFrequency(status, chosen.Value, "Trimode memory", chosen.Address);
        status.MemoryReadStatus = memoryStatus;
    }

    private void TryReadTrimodeDialFrequency(
        GatewayStatus status,
        ProcessMemoryReader reader,
        HashSet<int> expected)
    {
        // Fast path: re-read cached address if it still holds a plausible HF Hz (covers QSY at same cell).
        if (_cachedFrequencyAddress != IntPtr.Zero &&
            reader.TryReadInt32(_cachedFrequencyAddress, out var cachedValue) &&
            TrimodeFrequencyHeuristics.IsPlausibleHfHz(cachedValue))
        {
            ResetPendingDialCandidate();
            ApplyLiveFrequency(status, cachedValue, "Trimode dial", _cachedFrequencyAddress);
            status.MemoryReadStatus = expected.Contains(cachedValue)
                ? "OK cached dial (on scan list)"
                : "OK cached dial";
            return;
        }

        _cachedFrequencyAddress = IntPtr.Zero;

        var candidates = reader.FindInt32InRange(
            TrimodeFrequencyHeuristics.HfMinHz,
            TrimodeFrequencyHeuristics.HfMaxHz,
            TrimodeFrequencyHeuristics.DefaultMaxRangeCandidates);

        int? previousHz = _observedFrequencyKhz is > 0
            ? (int)(_observedFrequencyKhz.Value * 1000m)
            : null;

        var chosen = TrimodeFrequencyHeuristics.ChooseDialCandidate(
            candidates,
            _pendingDialAddress != IntPtr.Zero ? _pendingDialAddress : IntPtr.Zero,
            previousHz);

        if (chosen is null)
        {
            ResetPendingDialCandidate();
            NoteFrequencyMiss(status, candidates.Count == 0
                ? "No dial frequency candidate found"
                : $"Dial frequency ambiguous ({candidates.Count} candidates)");
            return;
        }

        // Require the same address across consecutive polls before promoting a new dial cell.
        if (_pendingDialAddress == chosen.Address && _pendingDialValue == chosen.Value)
        {
            _pendingDialConfirmations++;
        }
        else
        {
            _pendingDialAddress = chosen.Address;
            _pendingDialValue = chosen.Value;
            _pendingDialConfirmations = 1;
        }

        if (_pendingDialConfirmations < 2)
        {
            status.MemoryReadStatus = "Dial candidate pending confirmation";
            // Keep last confirmed observation during discovery; do not invent a new stamp yet.
            return;
        }

        _cachedFrequencyAddress = chosen.Address;
        ResetPendingDialCandidate();
        ApplyLiveFrequency(status, chosen.Value, "Trimode dial", chosen.Address);
        status.MemoryReadStatus = expected.Contains(chosen.Value)
            ? $"OK dial candidate ({candidates.Count})"
            : $"OK dial candidate off-list ({candidates.Count})";
    }

    private void ApplyLiveFrequency(GatewayStatus status, int frequencyHz, string source, IntPtr address)
    {
        var khz = frequencyHz / 1000.0m;
        var now = DateTimeOffset.UtcNow;
        status.CurrentFrequencyKhz = khz.ToString("0.000", CultureInfo.InvariantCulture);
        status.DialFrequencyKhz = ((frequencyHz - 1500) / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);
        status.LiveFrequencySource = source;
        status.FrequencyUpdatedAt = now;
        status.MemoryAddress = "0x" + address.ToInt64().ToString("X");
        _observedFrequencyKhz = khz;
        _observedFrequencySource = source;
        _observedFrequencyUpdatedAt = now;
        _frequencyMissStreak = 0;

        // Off-list dial leaves all Active flags false (INI may have seeded channel 1).
        foreach (var ch in status.ScanChannels)
            ch.Active = ch.FrequencyHz == frequencyHz;
    }

    private void NoteFrequencyMiss(GatewayStatus status, string message, bool clearCache = false)
    {
        status.MemoryReadStatus = message;
        if (clearCache)
            _cachedFrequencyAddress = IntPtr.Zero;

        _frequencyMissStreak++;
        if (_frequencyMissStreak >= FrequencyMissGracePolls)
            ClearFrequencyObservation();
    }

    private void ClearFrequencyObservation()
    {
        _cachedFrequencyAddress = IntPtr.Zero;
        ResetPendingDialCandidate();
        _observedFrequencyKhz = null;
        _observedFrequencySource = "Unknown";
        _observedFrequencyUpdatedAt = null;
        _lastScanMemoryValue = 0;
        _scanStagnantPolls = 0;
        _scanProbeAddresses.Clear();
        _scanProbeLastValues.Clear();
    }

    private void ResetPendingDialCandidate()
    {
        _pendingDialAddress = IntPtr.Zero;
        _pendingDialValue = 0;
        _pendingDialConfirmations = 0;
    }

    private void PollTrimodeScannerStatus(GatewayStatus status, bool force = true, bool allowRetry = false)
    {
        if (!status.TrimodeSeen)
        {
            status.ScannerEnabled = null;
            status.ScannerStatus = "Trimode Offline";
            status.CommandPortStatus = "Trimode offline";
            _lastKnownScannerEnabled = null;
            _lastScannerOkUtc = DateTime.MinValue;
            return;
        }

        var now = DateTime.UtcNow;
        if (!force &&
            _lastScannerAttemptUtc != DateTime.MinValue &&
            (now - _lastScannerAttemptUtc).TotalSeconds < ScannerPollMinIntervalSeconds)
        {
            ApplyCachedScannerStatus(status, "Deferred (live poll)");
            return;
        }

        _lastScannerAttemptUtc = now;
        var response = SendTrimodeCommand("SCAN");
        if (allowRetry && string.IsNullOrWhiteSpace(response))
            response = SendTrimodeCommand("SCAN");

        if (string.IsNullOrWhiteSpace(response))
        {
            ApplyStaleScannerStatus(status, "No response from command port");
            return;
        }

        status.CommandPortStatus = "OK";

        var m = Regex.Match(response, @"SCAN\s+(TRUE|FALSE)", RegexOptions.IgnoreCase);

        if (m.Success)
        {
            var enabled = m.Groups[1].Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
            status.ScannerEnabled = enabled;
            status.ScannerStatus = enabled ? "Scanning" : "Stopped";
            _lastKnownScannerEnabled = enabled;
            _lastScannerOkUtc = DateTime.UtcNow;
        }
        else
        {
            ApplyStaleScannerStatus(
                status,
                "Unexpected response: " + response.Replace("\r", " ").Replace("\n", " ").Trim());
        }
    }

    private void ApplyCachedScannerStatus(GatewayStatus status, string commandPortNote)
    {
        if (_lastKnownScannerEnabled is bool known)
        {
            status.ScannerEnabled = known;
            status.ScannerStatus = known ? "Scanning" : "Stopped";
            status.CommandPortStatus = commandPortNote;
            return;
        }

        status.ScannerEnabled = _status.ScannerEnabled;
        status.ScannerStatus = string.IsNullOrWhiteSpace(_status.ScannerStatus)
            ? "Unknown"
            : _status.ScannerStatus;
        status.CommandPortStatus = commandPortNote;
    }

    private void ApplyStaleScannerStatus(GatewayStatus status, string commandPortStatus)
    {
        if (_lastKnownScannerEnabled is bool known &&
            _lastScannerOkUtc != DateTime.MinValue &&
            (DateTime.UtcNow - _lastScannerOkUtc).TotalSeconds <= ScannerStaleGraceSeconds)
        {
            status.ScannerEnabled = known;
            status.ScannerStatus = known ? "Scanning" : "Stopped";
            status.CommandPortStatus = commandPortStatus + " (using last known)";
            return;
        }

        status.ScannerEnabled = null;
        status.ScannerStatus = "Unknown";
        status.CommandPortStatus = commandPortStatus;
    }

    private string SendTrimodeCommand(string command)
    {
        try
        {
            using var tcp = new TcpClient();
            var options = _options.CurrentValue;
            var connectTask = tcp.ConnectAsync(options.TrimodeHost, options.TrimodeCommandPort);
            if (!connectTask.Wait(TimeSpan.FromMilliseconds(500)))
                return "";

            using var stream = tcp.GetStream();
            stream.ReadTimeout = 500;
            stream.WriteTimeout = 500;

            var buffer = new byte[4096];

            // Drain any leftover banner bytes without a long fixed sleep.
            var drainDeadline = Environment.TickCount64 + 60;
            while (Environment.TickCount64 < drainDeadline && stream.DataAvailable)
                _ = stream.Read(buffer, 0, buffer.Length);

            var msg = Encoding.ASCII.GetBytes(command + "\r");
            stream.Write(msg, 0, msg.Length);

            var readDeadline = Environment.TickCount64 + 350;
            while (Environment.TickCount64 < readDeadline)
            {
                if (stream.DataAvailable)
                {
                    var count = stream.Read(buffer, 0, buffer.Length);
                    if (count > 0)
                        return Encoding.ASCII.GetString(buffer, 0, count);
                }

                Thread.Sleep(20);
            }

            return "";
        }
        catch
        {
            return "";
        }
    }

    private void ParseTrimodeIni(GatewayStatus status)
    {
        var options = _options.CurrentValue;

        if (!File.Exists(options.TrimodeIni)) return;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in SafeReadLines(options.TrimodeIni))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("[") || !line.Contains('=')) continue;

            var parts = line.Split('=', 2);
            values[parts[0].Trim()] = parts[1].Trim();
        }

        var channels = new List<ScanChannel>();

        for (int i = 1; i <= 8; i++)
        {
            values.TryGetValue($"Frequency {i}", out var freqRaw);
            if (!int.TryParse(freqRaw, out var hz)) continue;
            if (hz <= 0) continue;

            values.TryGetValue($"SC {i}", out var serviceCode);

            channels.Add(new ScanChannel
            {
                Number = i,
                FrequencyHz = hz,
                FrequencyKhz = (hz / 1000.0).ToString("0.000", CultureInfo.InvariantCulture),
                Mode = "PACTOR",
                Active = false,
                ServiceCode = serviceCode ?? ""
            });
        }

        if (channels.Count > 0)
        {
            channels[0].Active = true;
            status.CurrentFrequencyKhz = channels[0].FrequencyKhz;
            status.DialFrequencyKhz = ((channels[0].FrequencyHz - 1500) / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);
            status.CurrentMode = channels[0].Mode;
            status.ScanChannels = channels;
        }
    }

    private void ParseRelayLogs(
        GatewayStatus status,
        List<GatewayEvent> eventsList,
        Dictionary<string, int> stationCounts,
        List<StationConnection> stationConnections,
        List<HourlyActivity> hourlyActivity)
    {
        var options = _options.CurrentValue;

        foreach (var file in NewestFiles(options.RelayLogs, new[] { "Events*.log", "*.log" }, 200))
        {
            foreach (var line in SafeReadLines(file))
            {
                var ts = ExtractTimestamp(line);
                if (ts is null) continue;

                if (line.Contains("RMS Relay started", StringComparison.OrdinalIgnoreCase))
                {
                    status.LastRelayEvent = ts;
                    SetIfNewer(ts, value => status.LastRelayStart = value, status.LastRelayStart);
                    eventsList.Add(new GatewayEvent(ts, "Relay", "Startup", "RMS Relay started"));
                }

                if (line.Contains("RMS Relay is stopping", StringComparison.OrdinalIgnoreCase))
                {
                    status.LastRelayEvent = ts;
                    eventsList.Add(new GatewayEvent(ts, "Relay", "Stopping", "RMS Relay stopping"));
                }

                var m = Regex.Match(line, @"HF client connection from\s+([A-Z0-9]+)", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var station = m.Groups[1].Value.ToUpperInvariant();
                    status.LastStation = station;
                    status.LastRelayEvent = ts;
                    stationCounts[station] = stationCounts.TryGetValue(station, out var c) ? c + 1 : 1;
                    stationConnections.Add(new StationConnection
                    {
                        Timestamp = ts,
                        Station = station,
                        Source = "Relay",
                        Detail = "HF client connection"
                    });
                    IncrementHourlyActivity(hourlyActivity, ParseAnyTime(ts), a => a.RelayConnections++);
                    eventsList.Add(new GatewayEvent(ts, "Relay", "HF Connection", $"HF client connection from {station}"));
                }
            }
        }
    }

    private void ParseTrimodeLogs(
        GatewayStatus status,
        List<GatewayEvent> eventsList,
        List<HourlyActivity> hourlyActivity)
    {
        DateTime newestTrimodeEvent = DateTime.MinValue;
        DateTime newestSfiTime = DateTime.MinValue;
        int? newestSfi = null;

        DateTime newestConnectionTime = DateTime.MinValue;
        DateTime newestDisconnectTime = DateTime.MinValue;
        var options = _options.CurrentValue;

        foreach (var file in NewestFiles(options.TrimodeLogs, new[] { "*.log" }, 20))
        {
            foreach (var line in SafeReadLines(file))
            {
                var ts = ExtractTimestamp(line);
                if (ts is null) continue;

                var dt = ParseAnyTime(ts) ?? DateTime.MinValue;

                if (dt > newestTrimodeEvent)
                {
                    newestTrimodeEvent = dt;
                    status.LastTrimodeEvent = ts;
                }

                if (line.Contains("RMS Trimode started", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("RMS Trimode startup", StringComparison.OrdinalIgnoreCase))
                {
                    SetIfNewer(ts, value => status.LastTrimodeStart = value, status.LastTrimodeStart);
                    eventsList.Add(new GatewayEvent(ts, "Trimode", "Startup", "RMS Trimode started"));
                }

                var sfi = Regex.Match(line, @"SFI\s*=\s*(\d+)", RegexOptions.IgnoreCase);
                if (sfi.Success && int.TryParse(sfi.Groups[1].Value, out var sfiValue))
                {
                    if (dt > newestSfiTime)
                    {
                        newestSfiTime = dt;
                        newestSfi = sfiValue;
                    }

                    eventsList.Add(new GatewayEvent(ts, "Trimode", "SFI", $"Solar Flux Index {sfiValue}"));
                }

                if (line.Contains("Active Pactor channels reported", StringComparison.OrdinalIgnoreCase))
                    eventsList.Add(new GatewayEvent(ts, "Trimode", "Channel Report", "Active PACTOR channels reported"));

                if (line.Contains("Modem reported ARQ connection", StringComparison.OrdinalIgnoreCase))
                {
                    if (dt > newestConnectionTime)
                    {
                        newestConnectionTime = dt;
                        status.LastConnection = ts;
                    }

                    if (IsToday(ts)) status.SessionsToday++;
                    IncrementHourlyActivity(hourlyActivity, dt, a => a.TrimodeConnections++);

                    eventsList.Add(new GatewayEvent(ts, "Trimode", "Connection", "PACTOR ARQ connection"));
                }

                if (line.Contains("Pactor modem reported Disconnected", StringComparison.OrdinalIgnoreCase))
                {
                    if (dt > newestDisconnectTime)
                    {
                        newestDisconnectTime = dt;
                        status.LastDisconnect = ts;
                    }

                    IncrementHourlyActivity(hourlyActivity, dt, a => a.Disconnects++);
                    eventsList.Add(new GatewayEvent(ts, "Trimode", "Disconnected", "PACTOR modem disconnected"));
                }

                if (line.Contains("Successfully reported RMS Trimode usage statistics", StringComparison.OrdinalIgnoreCase))
                    eventsList.Add(new GatewayEvent(ts, "Trimode", "Stats", "Usage statistics reported"));
            }
        }

        if (newestSfi.HasValue)
            status.LastSfi = newestSfi.Value;
    }

    private static List<HourlyActivity> CreateHourlyActivity()
    {
        return Enumerable.Range(0, 24)
            .Select(hour => new HourlyActivity { Hour = $"{hour:00}:00" })
            .ToList();
    }

    private static void IncrementHourlyActivity(List<HourlyActivity> hourlyActivity, DateTime? timestamp, Action<HourlyActivity> increment)
    {
        if (timestamp?.Date != DateTime.Today)
            return;

        increment(hourlyActivity[timestamp.Value.Hour]);
    }

    private static List<UptimeMetric> CreateUptimeMetrics(GatewayStatus status)
    {
        return new List<UptimeMetric>
        {
            CreateUptimeMetric("RMS Relay", status.RelayRunning == true, status.LastRelayStart),
            CreateUptimeMetric("RMS Trimode", status.TrimodeSeen, status.LastTrimodeStart)
        };
    }

    private static UptimeMetric CreateUptimeMetric(string name, bool running, string? lastStarted)
    {
        var started = string.IsNullOrWhiteSpace(lastStarted)
            ? null
            : ParseAnyTime(lastStarted);

        var hours = running && started.HasValue
            ? Math.Max(0, (DateTime.Now - started.Value).TotalHours)
            : 0;

        return new UptimeMetric
        {
            Name = name,
            Running = running,
            LastStarted = lastStarted ?? "",
            Hours = Math.Round(hours, 2),
            Display = running
                ? started.HasValue ? FormatDuration(DateTime.Now - started.Value) : "Running, start time unknown"
                : "Stopped"
        };
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h";

        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";

        return $"{Math.Max(0, duration.Minutes)}m";
    }

    private static void SetIfNewer(string timestamp, Action<string> setValue, string? currentValue)
    {
        var next = ParseAnyTime(timestamp);
        var current = string.IsNullOrWhiteSpace(currentValue) ? null : ParseAnyTime(currentValue);

        if (next.HasValue && (!current.HasValue || next.Value > current.Value))
            setValue(timestamp);
    }

    private static void ApplyProcessStartTimes(GatewayStatus status)
    {
        var relayStart = GetProcessStartTime("RMS Relay");
        if (relayStart.HasValue)
            status.LastRelayStart = relayStart.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var trimodeStart = GetProcessStartTime("RMS Trimode");
        if (trimodeStart.HasValue)
            status.LastTrimodeStart = trimodeStart.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static bool IsProcessRunning(string processName)
    {
        try
        {
            return Process.GetProcesses()
                .Any(p => p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static DateTime? GetProcessStartTime(string processName)
    {
        try
        {
            var process = Process.GetProcesses()
                .FirstOrDefault(p => p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));

            return process?.StartTime;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeReadLines(string file)
    {
        // Share read/write so Trimode/Relay can keep writing logs/INI while we observe.
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (!reader.EndOfStream)
                lines.Add(reader.ReadLine() ?? "");
            return lines;
        }
        catch { return Enumerable.Empty<string>(); }
    }

    private static IEnumerable<string> NewestFiles(string folder, string[] patterns, int limit)
    {
        try
        {
            if (!Directory.Exists(folder)) return Enumerable.Empty<string>();

            return patterns
                .SelectMany(p => Directory.GetFiles(folder, p))
                .Distinct()
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(limit)
                .ToList();
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    private static string? ExtractTimestamp(string line)
    {
        var m1 = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})");
        if (m1.Success) return m1.Groups[1].Value;

        var m2 = Regex.Match(line, @"^(\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2})");
        if (m2.Success) return m2.Groups[1].Value;

        return null;
    }

    private static DateTime? ParseAnyTime(string ts)
    {
        string[] formats = { "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss" };

        foreach (var fmt in formats)
        {
            if (DateTime.TryParseExact(ts, fmt, null, DateTimeStyles.None, out var dt))
                return dt;
        }

        return null;
    }

    private static bool IsToday(string ts)
    {
        var dt = ParseAnyTime(ts);
        return dt?.Date == DateTime.Today;
    }
}
