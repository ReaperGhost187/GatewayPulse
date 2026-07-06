using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace GatewayPulse.Core;

public sealed class GatewayPulseService
{
    private readonly GatewayPulseOptions _options;
    private readonly PushoverService _pushover;
    private readonly object _lock = new();

    private GatewayStatus _status = new();
    private string _lastAlertStateKey = "";
    private DateTime _lastAlertSentUtc = DateTime.MinValue;
    private IntPtr _cachedFrequencyAddress = IntPtr.Zero;

    public GatewayPulseService(
        IOptions<GatewayPulseOptions> options,
        PushoverService pushover)
    {
        _options = options.Value;
        _pushover = pushover;
        Refresh();
    }

    public GatewayStatus GetStatus()
    {
        lock (_lock)
        {
            Refresh();
            return _status;
        }
    }

    private void Refresh()
    {
        var status = new GatewayStatus
        {
            GatewayName = _options.GatewayName,
            Callsign = _options.Callsign,
            LastScan = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };

        var eventsList = new List<GatewayEvent>();
        var stationCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        status.RelayRunning = IsProcessRunning("RMS Relay");
        status.TrimodeSeen = IsProcessRunning("RMS Trimode");

        ParseRelayLogs(status, eventsList, stationCounts);
        ParseTrimodeLogs(status, eventsList);
        ParseTrimodeIni(status);
        PollTrimodeScannerStatus(status);
        TryReadTrimodeMemory(status);

        status.Healthy =
            status.RelayRunning == true &&
            status.TrimodeSeen &&
            status.ScannerEnabled != false;

        EvaluateAlerts(status);

        status.StationCounts = stationCounts
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new { station = kv.Key, count = kv.Value })
            .Cast<object>()
            .ToList();

        status.RecentEvents = eventsList
            .DistinctBy(e => $"{e.Timestamp}|{e.Source}|{e.Type}|{e.Detail}")
            .OrderByDescending(e => ParseAnyTime(e.Timestamp) ?? DateTime.MinValue)
            .Take(80)
            .ToList();

        _status = status;
    }

    private void EvaluateAlerts(GatewayStatus status)
    {
        var problems = new List<string>();

        if (status.RelayRunning != true)
            problems.Add("RMS Relay is offline");

        if (!status.TrimodeSeen)
            problems.Add("RMS Trimode is offline");

        if (status.TrimodeSeen && status.ScannerEnabled == false)
            problems.Add("Scanner is stopped");

        if (status.TrimodeSeen && status.ScannerEnabled is null)
            problems.Add("Trimode command port is not responding");

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
        _lastAlertSentUtc = now;

        if (problems.Count > 0)
        {
            _ = _pushover.SendAsync(
                "🔴 Gateway Pulse Alert",
                $"{status.GatewayName}\n\n{string.Join("\n", problems)}\n\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        else if (_pushover.SendRecoveryAlerts)
        {
            _ = _pushover.SendAsync(
                "🟢 Gateway Pulse Recovery",
                $"{status.GatewayName}\n\nGateway health is restored.\n\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
    }

    private void TryReadTrimodeMemory(GatewayStatus status)
    {
        if (!status.TrimodeSeen)
        {
            status.MemoryReadStatus = "Trimode offline";
            _cachedFrequencyAddress = IntPtr.Zero;
            return;
        }

        var expected = status.ScanChannels
            .Where(c => c.FrequencyHz > 0)
            .Select(c => c.FrequencyHz)
            .Distinct()
            .ToHashSet();

        if (expected.Count == 0)
        {
            status.MemoryReadStatus = "No configured frequencies to match";
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
                status.MemoryReadStatus = "Trimode process not found";
                _cachedFrequencyAddress = IntPtr.Zero;
                return;
            }

            using var reader = new ProcessMemoryReader(proc.Id);

            if (_cachedFrequencyAddress != IntPtr.Zero &&
                reader.TryReadInt32(_cachedFrequencyAddress, out var cachedValue) &&
                expected.Contains(cachedValue))
            {
                ApplyLiveFrequency(status, cachedValue, "Trimode memory", _cachedFrequencyAddress);
                status.MemoryReadStatus = "OK cached";
                return;
            }

            _cachedFrequencyAddress = IntPtr.Zero;

            var candidates = reader.FindInt32Candidates(expected, maxCandidates: 40);

            var chosen = candidates.FirstOrDefault(c => !c.LooksLikeArray);
            if (chosen.Address == IntPtr.Zero && candidates.Count > 0)
                chosen = candidates[0];

            if (chosen.Address == IntPtr.Zero)
            {
                status.MemoryReadStatus = "No frequency candidate found";
                return;
            }

            _cachedFrequencyAddress = chosen.Address;
            ApplyLiveFrequency(status, chosen.Value, "Trimode memory", chosen.Address);
            status.MemoryReadStatus = chosen.LooksLikeArray
                ? $"Candidate found, may be config array ({candidates.Count})"
                : $"OK candidate found ({candidates.Count})";
        }
        catch (Exception ex)
        {
            status.MemoryReadStatus = "Memory read error: " + ex.GetType().Name;
            _cachedFrequencyAddress = IntPtr.Zero;
        }
    }

    private static void ApplyLiveFrequency(GatewayStatus status, int frequencyHz, string source, IntPtr address)
    {
        var khz = frequencyHz / 1000.0;
        status.CurrentFrequencyKhz = khz.ToString("0.000", CultureInfo.InvariantCulture);
        status.DialFrequencyKhz = ((frequencyHz - 1500) / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);
        status.LiveFrequencySource = source;
        status.MemoryAddress = "0x" + address.ToInt64().ToString("X");

        foreach (var ch in status.ScanChannels)
            ch.Active = ch.FrequencyHz == frequencyHz;
    }

    private void PollTrimodeScannerStatus(GatewayStatus status)
    {
        if (!status.TrimodeSeen)
        {
            status.ScannerEnabled = null;
            status.ScannerStatus = "Trimode Offline";
            status.CommandPortStatus = "Trimode offline";
            return;
        }

        var response = SendTrimodeCommand("SCAN");

        if (string.IsNullOrWhiteSpace(response))
        {
            status.ScannerEnabled = null;
            status.ScannerStatus = "Unknown";
            status.CommandPortStatus = "No response from command port";
            return;
        }

        status.CommandPortStatus = "OK";

        var m = Regex.Match(response, @"SCAN\s+(TRUE|FALSE)", RegexOptions.IgnoreCase);

        if (m.Success)
        {
            var enabled = m.Groups[1].Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
            status.ScannerEnabled = enabled;
            status.ScannerStatus = enabled ? "Scanning" : "Stopped";
        }
        else
        {
            status.ScannerEnabled = null;
            status.ScannerStatus = "Unknown";
            status.CommandPortStatus = "Unexpected response: " + response.Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }

    private string SendTrimodeCommand(string command)
    {
        try
        {
            using var tcp = new TcpClient();
            var connectTask = tcp.ConnectAsync(_options.TrimodeHost, _options.TrimodeCommandPort);
            if (!connectTask.Wait(TimeSpan.FromMilliseconds(750)))
                return "";

            using var stream = tcp.GetStream();
            stream.ReadTimeout = 750;
            stream.WriteTimeout = 750;

            var buffer = new byte[4096];

            Thread.Sleep(80);
            if (stream.DataAvailable)
                _ = stream.Read(buffer, 0, buffer.Length);

            var msg = Encoding.ASCII.GetBytes(command + "\r");
            stream.Write(msg, 0, msg.Length);

            Thread.Sleep(150);

            if (!stream.DataAvailable)
                return "";

            var count = stream.Read(buffer, 0, buffer.Length);
            return Encoding.ASCII.GetString(buffer, 0, count);
        }
        catch
        {
            return "";
        }
    }

    private void ParseTrimodeIni(GatewayStatus status)
    {
        if (!File.Exists(_options.TrimodeIni)) return;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in SafeReadLines(_options.TrimodeIni))
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

    private void ParseRelayLogs(GatewayStatus status, List<GatewayEvent> eventsList, Dictionary<string, int> stationCounts)
    {
        foreach (var file in NewestFiles(_options.RelayLogs, new[] { "Events*.log", "*.log" }, 30))
        {
            foreach (var line in SafeReadLines(file))
            {
                var ts = ExtractTimestamp(line);
                if (ts is null) continue;

                if (line.Contains("RMS Relay started", StringComparison.OrdinalIgnoreCase))
                {
                    status.LastRelayEvent = ts;
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
                    eventsList.Add(new GatewayEvent(ts, "Relay", "HF Connection", $"HF client connection from {station}"));
                }
            }
        }
    }

    private void ParseTrimodeLogs(GatewayStatus status, List<GatewayEvent> eventsList)
    {
        DateTime newestTrimodeEvent = DateTime.MinValue;
        DateTime newestSfiTime = DateTime.MinValue;
        int? newestSfi = null;

        DateTime newestConnectionTime = DateTime.MinValue;
        DateTime newestDisconnectTime = DateTime.MinValue;

        foreach (var file in NewestFiles(_options.TrimodeLogs, new[] { "*.log" }, 20))
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

                    eventsList.Add(new GatewayEvent(ts, "Trimode", "Connection", "PACTOR ARQ connection"));
                }

                if (line.Contains("Pactor modem reported Disconnected", StringComparison.OrdinalIgnoreCase))
                {
                    if (dt > newestDisconnectTime)
                    {
                        newestDisconnectTime = dt;
                        status.LastDisconnect = ts;
                    }

                    eventsList.Add(new GatewayEvent(ts, "Trimode", "Disconnected", "PACTOR modem disconnected"));
                }

                if (line.Contains("Successfully reported RMS Trimode usage statistics", StringComparison.OrdinalIgnoreCase))
                    eventsList.Add(new GatewayEvent(ts, "Trimode", "Stats", "Usage statistics reported"));
            }
        }

        if (newestSfi.HasValue)
            status.LastSfi = newestSfi.Value;
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

    private static IEnumerable<string> SafeReadLines(string file)
    {
        try { return File.ReadLines(file); }
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
