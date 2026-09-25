using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GatewayPulse.Core;

/// <summary>
/// One unique RMS Relay station contact: an "HF client connection from CALLSIGN" log line.
/// Identity is (gateway-local timestamp, station); the same line appearing in more than one
/// Relay log file (or twice in one file) is the same contact.
/// </summary>
public readonly record struct RelayStationContact(DateTime LocalTime, string Station)
{
    public string Key => $"{LocalTime:yyyyMMddHHmmss}|{Station}";
}

/// <summary>Pure parsing and de-duplication for Relay station contacts.</summary>
public static class RelayStationLog
{
    private static readonly Regex ConnectionPattern =
        // Keeps an SSID suffix (KX7ABC-5) so SSIDs are counted as distinct stations.
        new(@"HF client connection from\s+([A-Z0-9]+(?:-\d{1,2})?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TimestampPattern =
        new(@"^(\d{4}[-/]\d{2}[-/]\d{2} \d{2}:\d{2}:\d{2})", RegexOptions.Compiled);

    private static readonly string[] TimestampFormats = { "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss" };

    public static bool TryParseContact(string line, out RelayStationContact contact)
    {
        contact = default;
        var timestamp = TimestampPattern.Match(line);
        if (!timestamp.Success) return false;

        var connection = ConnectionPattern.Match(line);
        if (!connection.Success) return false;

        if (!DateTime.TryParseExact(
                timestamp.Groups[1].Value,
                TimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTime))
            return false;

        contact = new RelayStationContact(localTime, connection.Groups[1].Value.ToUpperInvariant());
        return true;
    }

    public static IEnumerable<RelayStationContact> ParseLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (TryParseContact(line, out var contact))
                yield return contact;
        }
    }

    /// <summary>Unique contacts, newest first (ties ordered by station for stable paging).</summary>
    public static List<RelayStationContact> DeduplicateNewestFirst(IEnumerable<RelayStationContact> contacts) =>
        contacts
            .DistinctBy(c => c.Key)
            .OrderByDescending(c => c.LocalTime)
            .ThenBy(c => c.Station, StringComparer.Ordinal)
            .ToList();
}

public sealed record StationHistoryCoverage(
    DateTimeOffset? OldestContact,
    DateTimeOffset? NewestContact,
    DateTimeOffset? OldestLogContact,
    int LogFilesScanned,
    bool LogFolderAvailable,
    int ArchivedContacts,
    DateTimeOffset? ArchiveStartedAt);

public sealed record StationTotal(string Station, int Contacts, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

public sealed record DailyContactCount(string Date, int Contacts);

public sealed record HourlyContactCount(int Hour, int Contacts);

public sealed record StationContactDto(string Id, DateTimeOffset Timestamp, string Station, string Source);

public sealed record StationHistorySummary(
    int TotalContacts,
    int UniqueStations,
    int ContactsToday,
    StationContactDto? LatestContact,
    IReadOnlyList<StationTotal> Stations,
    IReadOnlyList<DailyContactCount> Daily,
    IReadOnlyList<HourlyContactCount> Hourly,
    StationHistoryCoverage Coverage);

public sealed record StationDetail(
    string Station,
    int Contacts,
    int Rank,
    int TotalStations,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    IReadOnlyList<DailyContactCount> Daily,
    IReadOnlyList<HourlyContactCount> Hourly,
    StationHistoryCoverage Coverage);

public sealed record StationContactPage(
    IReadOnlyList<StationContactDto> Items,
    string? NextCursor,
    int TotalMatching);

/// <summary>
/// Indexes every RMS Relay log for station contacts and keeps a local archive so contacts
/// survive after RMS Relay prunes old log files. Unchanged log files are not re-read.
/// </summary>
public sealed class StationHistoryStore
{
    public const int MaxArchivedContacts = 100_000;
    public const int MaxPageSize = 200;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly Func<string> _relayLogFolder;
    private readonly string? _archivePath;
    private readonly Func<DateTime> _localNow;
    private readonly TimeZoneInfo _timeZone;
    private readonly TimeSpan _minRefreshInterval;
    private readonly object _sync = new();

    private readonly Dictionary<string, (DateTime WriteUtc, long Length, List<RelayStationContact> Contacts)> _fileIndex =
        new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, RelayStationContact> _archive = new(StringComparer.Ordinal);
    private DateTimeOffset? _archiveStartedAt;
    private List<RelayStationContact> _contacts = new();
    private DateTime? _oldestLogContact;
    private bool _logFolderAvailable;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private bool _archiveLoaded;

    public StationHistoryStore(
        Func<string> relayLogFolder,
        string? archivePath,
        Func<DateTime>? localNow = null,
        TimeZoneInfo? timeZone = null,
        TimeSpan? minRefreshInterval = null)
    {
        _relayLogFolder = relayLogFolder;
        _archivePath = archivePath;
        _localNow = localNow ?? (() => DateTime.Now);
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _minRefreshInterval = minRefreshInterval ?? TimeSpan.FromSeconds(15);
    }

    public void Refresh(bool force = false)
    {
        lock (_sync)
        {
            if (!force && DateTime.UtcNow - _lastRefreshUtc < _minRefreshInterval)
                return;
            _lastRefreshUtc = DateTime.UtcNow;

            LoadArchiveIfNeeded();
            IndexLogFiles();

            var fromLogs = _fileIndex.Values.SelectMany(f => f.Contacts).ToList();
            _oldestLogContact = fromLogs.Count == 0 ? null : fromLogs.Min(c => c.LocalTime);

            var added = false;
            foreach (var contact in fromLogs)
            {
                if (_archive.TryAdd(contact.Key, contact))
                    added = true;
            }

            _contacts = RelayStationLog.DeduplicateNewestFirst(_archive.Values);
            if (_contacts.Count > MaxArchivedContacts)
            {
                _contacts = _contacts.Take(MaxArchivedContacts).ToList();
                _archive = _contacts.ToDictionary(c => c.Key, StringComparer.Ordinal);
                added = true;
            }

            if (added)
                SaveArchive();
        }
    }

    public StationHistoryCoverage GetCoverage()
    {
        lock (_sync)
            return Coverage();
    }

    public StationHistorySummary GetSummary(int days)
    {
        Refresh();
        lock (_sync)
        {
            var today = _localNow().Date;
            var stations = _contacts
                .GroupBy(c => c.Station, StringComparer.Ordinal)
                .Select(g => new StationTotal(g.Key, g.Count(), ToOffset(g.Min(c => c.LocalTime)), ToOffset(g.Max(c => c.LocalTime))))
                .OrderByDescending(s => s.Contacts)
                .ThenByDescending(s => s.LastSeen)
                .ThenBy(s => s.Station, StringComparer.Ordinal)
                .ToList();

            return new StationHistorySummary(
                TotalContacts: _contacts.Count,
                UniqueStations: stations.Count,
                ContactsToday: _contacts.Count(c => c.LocalTime.Date == today),
                LatestContact: _contacts.Count == 0 ? null : ToDto(_contacts[0]),
                Stations: stations,
                Daily: DailyCounts(_contacts, today, days),
                Hourly: HourlyCounts(_contacts),
                Coverage: Coverage());
        }
    }

    public StationDetail? GetStation(string station, int days)
    {
        Refresh();
        var callsign = station.Trim().ToUpperInvariant();
        lock (_sync)
        {
            var contacts = _contacts.Where(c => c.Station == callsign).ToList();
            if (contacts.Count == 0) return null;

            var ranking = _contacts
                .GroupBy(c => c.Station, StringComparer.Ordinal)
                .Select(g => (Station: g.Key, Count: g.Count(), Last: g.Max(c => c.LocalTime)))
                .OrderByDescending(s => s.Count)
                .ThenByDescending(s => s.Last)
                .ThenBy(s => s.Station, StringComparer.Ordinal)
                .ToList();

            return new StationDetail(
                callsign,
                contacts.Count,
                ranking.FindIndex(s => s.Station == callsign) + 1,
                ranking.Count,
                ToOffset(contacts.Min(c => c.LocalTime)),
                ToOffset(contacts.Max(c => c.LocalTime)),
                DailyCounts(contacts, _localNow().Date, days),
                HourlyCounts(contacts),
                Coverage());
        }
    }

    /// <summary>Newest-first page. <paramref name="cursor"/> is the opaque value from a previous page.</summary>
    public StationContactPage GetContacts(string? station, string? cursor, int limit)
    {
        Refresh();
        var callsign = string.IsNullOrWhiteSpace(station) ? null : station.Trim().ToUpperInvariant();
        var pageSize = Math.Clamp(limit, 1, MaxPageSize);

        lock (_sync)
        {
            var matching = callsign is null ? _contacts : _contacts.Where(c => c.Station == callsign).ToList();

            var start = 0;
            if (TryDecodeCursor(cursor, out var after))
            {
                // Contacts are ordered (time desc, station asc); resume strictly after the cursor.
                start = matching.FindIndex(c =>
                    c.LocalTime < after.LocalTime ||
                    (c.LocalTime == after.LocalTime && string.CompareOrdinal(c.Station, after.Station) > 0));
                if (start < 0) start = matching.Count;
            }

            var items = matching.Skip(start).Take(pageSize).ToList();
            var hasMore = start + items.Count < matching.Count;

            return new StationContactPage(
                items.Select(ToDto).ToList(),
                hasMore && items.Count > 0 ? EncodeCursor(items[^1]) : null,
                matching.Count);
        }
    }

    // MARK: - Aggregation

    private static List<DailyContactCount> DailyCounts(IEnumerable<RelayStationContact> contacts, DateTime today, int days)
    {
        var window = Math.Clamp(days, 1, 366);
        var start = today.AddDays(-(window - 1));
        var counts = contacts
            .Where(c => c.LocalTime.Date >= start && c.LocalTime.Date <= today)
            .GroupBy(c => c.LocalTime.Date)
            .ToDictionary(g => g.Key, g => g.Count());

        return Enumerable.Range(0, window)
            .Select(offset => start.AddDays(offset))
            .Select(date => new DailyContactCount(
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                counts.GetValueOrDefault(date)))
            .ToList();
    }

    private static List<HourlyContactCount> HourlyCounts(IEnumerable<RelayStationContact> contacts)
    {
        var counts = new int[24];
        foreach (var contact in contacts)
            counts[contact.LocalTime.Hour]++;
        return counts.Select((count, hour) => new HourlyContactCount(hour, count)).ToList();
    }

    private StationHistoryCoverage Coverage() => new(
        OldestContact: _contacts.Count == 0 ? null : ToOffset(_contacts[^1].LocalTime),
        NewestContact: _contacts.Count == 0 ? null : ToOffset(_contacts[0].LocalTime),
        OldestLogContact: _oldestLogContact.HasValue ? ToOffset(_oldestLogContact.Value) : null,
        LogFilesScanned: _fileIndex.Count,
        LogFolderAvailable: _logFolderAvailable,
        ArchivedContacts: _archive.Count,
        ArchiveStartedAt: _archiveStartedAt);

    private StationContactDto ToDto(RelayStationContact contact) =>
        new(contact.Key, ToOffset(contact.LocalTime), contact.Station, "Relay");

    private DateTimeOffset ToOffset(DateTime localTime) =>
        new(DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), _timeZone.GetUtcOffset(localTime));

    // MARK: - Cursor

    private static string EncodeCursor(RelayStationContact contact) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(contact.Key));

    private static bool TryDecodeCursor(string? cursor, out RelayStationContact contact)
    {
        contact = default;
        if (string.IsNullOrWhiteSpace(cursor)) return false;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|', 2);
            if (parts.Length != 2 ||
                !DateTime.TryParseExact(parts[0], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                return false;
            contact = new RelayStationContact(time, parts[1]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // MARK: - Log indexing

    private void IndexLogFiles()
    {
        var folder = _relayLogFolder();
        _logFolderAvailable = Directory.Exists(folder);
        if (!_logFolderAvailable)
        {
            _fileIndex.Clear();
            return;
        }

        string[] files;
        try
        {
            // Include subfolders (archived/rotated logs). Duplicate copies are harmless: contacts
            // are unique by timestamp + station.
            files = Directory.GetFiles(folder, "*.log", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _fileIndex.Keys.Where(k => !present.Contains(k)).ToList())
            _fileIndex.Remove(stale);

        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (_fileIndex.TryGetValue(file, out var cached) &&
                    cached.WriteUtc == info.LastWriteTimeUtc &&
                    cached.Length == info.Length)
                    continue;

                var contacts = RelayStationLog.ParseLines(ReadSharedLines(file)).ToList();
                _fileIndex[file] = (info.LastWriteTimeUtc, info.Length, contacts);
            }
            catch (IOException)
            {
                // Relay may be rotating the file; pick it up on the next refresh.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static IEnumerable<string> ReadSharedLines(string file)
    {
        // Share read/write so RMS Relay can keep writing while we observe.
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines;
    }

    // MARK: - Archive

    private sealed record ArchiveFile(DateTimeOffset StartedAt, List<ArchivedContact> Contacts);

    private sealed record ArchivedContact(string Time, string Station);

    private void LoadArchiveIfNeeded()
    {
        if (_archiveLoaded) return;
        _archiveLoaded = true;

        if (string.IsNullOrWhiteSpace(_archivePath) || !File.Exists(_archivePath))
            return;

        try
        {
            var payload = JsonSerializer.Deserialize<ArchiveFile>(File.ReadAllText(_archivePath), JsonOptions);
            if (payload is null) return;

            _archiveStartedAt = payload.StartedAt;
            foreach (var entry in payload.Contacts)
            {
                if (DateTime.TryParseExact(entry.Time, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) &&
                    !string.IsNullOrWhiteSpace(entry.Station))
                {
                    var contact = new RelayStationContact(time, entry.Station.ToUpperInvariant());
                    _archive.TryAdd(contact.Key, contact);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged archive must not break status; logs are re-indexed and the archive rewritten.
        }
    }

    private void SaveArchive()
    {
        if (string.IsNullOrWhiteSpace(_archivePath)) return;

        try
        {
            _archiveStartedAt ??= new DateTimeOffset(_localNow(), _timeZone.GetUtcOffset(_localNow()));
            var payload = new ArchiveFile(
                _archiveStartedAt.Value,
                _contacts
                    .Select(c => new ArchivedContact(c.LocalTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), c.Station))
                    .ToList());

            var directory = Path.GetDirectoryName(_archivePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temporaryPath = _archivePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload, JsonOptions));
            File.Move(temporaryPath, _archivePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Archive is best-effort; history is still served from the logs in memory.
        }
    }
}
