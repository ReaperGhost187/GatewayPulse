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

/// <summary>A station connection from either Relay or a Trimode ADIF session.</summary>
public readonly record struct StationHistoryContact(DateTime LocalTime, string Station, string Source)
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
    DateTimeOffset? ArchiveStartedAt,
    bool ArchiveHealthy,
    int TrimodeFilesScanned = 0,
    bool TrimodeFolderAvailable = false,
    DateTimeOffset? OldestTrimodeContact = null);

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
/// Indexes RMS Relay connection logs and RMS Trimode ADIF sessions, reconciling
/// overlapping records before archiving them for long-term history.
/// </summary>
public sealed class StationHistoryStore
{
    public const int MaxPageSize = 200;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly Func<string> _relayLogFolder;
    private readonly Func<string>? _trimodeLogFolder;
    private readonly string? _archivePath;
    private readonly Func<DateTime> _localNow;
    private readonly TimeZoneInfo _timeZone;
    private readonly TimeSpan _minRefreshInterval;
    private readonly object _sync = new();

    private readonly Dictionary<string, (DateTime WriteUtc, long Length, List<StationHistoryContact> Contacts)> _fileIndex =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, (DateTime WriteUtc, long Length, List<StationHistoryContact> Contacts)> _trimodeFileIndex =
        new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, StationHistoryContact> _archive = new(StringComparer.Ordinal);
    private DateTimeOffset? _archiveStartedAt;
    private List<StationHistoryContact> _contacts = new();
    private DateTime? _oldestLogContact;
    private DateTime? _oldestTrimodeContact;
    private bool _logFolderAvailable;
    private bool _trimodeFolderAvailable;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private bool _archiveLoaded;
    private bool _archiveDirty;
    private bool _archiveHealthy;
    private bool _archiveWasCorrupt;
    private bool _archivePreservationFailed;
    private int _persistedContactCount;

    public StationHistoryStore(
        Func<string> relayLogFolder,
        string? archivePath,
        Func<DateTime>? localNow = null,
        TimeZoneInfo? timeZone = null,
        TimeSpan? minRefreshInterval = null,
        Func<string>? trimodeLogFolder = null)
    {
        _relayLogFolder = relayLogFolder;
        _trimodeLogFolder = trimodeLogFolder;
        _archivePath = archivePath;
        _localNow = localNow ?? (() => DateTime.Now);
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _minRefreshInterval = minRefreshInterval ?? TimeSpan.FromSeconds(15);
        _archiveHealthy = !string.IsNullOrWhiteSpace(archivePath);
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
            IndexTrimodeFiles();

            var fromLogs = _fileIndex.Values.SelectMany(f => f.Contacts).ToList();
            var fromTrimode = _trimodeFileIndex.Values.SelectMany(f => f.Contacts).ToList();
            _oldestLogContact = fromLogs.Count == 0 ? null : fromLogs.Min(c => c.LocalTime);
            _oldestTrimodeContact = fromTrimode.Count == 0 ? null : fromTrimode.Min(c => c.LocalTime);

            var added = false;
            foreach (var contact in fromLogs)
            {
                if (!_archive.TryGetValue(contact.Key, out var previous) || previous.Source != "Relay")
                {
                    _archive[contact.Key] = contact;
                    added = true;
                }
            }

            // Trimode ADIF and Relay can describe the same session with a one-second
            // timestamp difference. Prefer the Relay event and keep one contact.
            var relayKeys = _archive.Values.Where(c => c.Source == "Relay")
                .Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            bool MatchesRelay(StationHistoryContact trimode) =>
                Enumerable.Range(-2, 5).Any(seconds => relayKeys.Contains(
                    new StationHistoryContact(trimode.LocalTime.AddSeconds(seconds), trimode.Station, "Relay").Key));

            foreach (var contact in fromTrimode)
            {
                if (!MatchesRelay(contact) && _archive.TryAdd(contact.Key, contact))
                    added = true;
            }

            foreach (var trimode in _archive.Values.Where(c => c.Source == "Trimode").ToList())
            {
                if (MatchesRelay(trimode))
                {
                    _archive.Remove(trimode.Key);
                    added = true;
                }
            }

            _contacts = _archive.Values
                .OrderByDescending(c => c.LocalTime)
                .ThenBy(c => c.Station, StringComparer.Ordinal)
                .ToList();

            _archiveDirty |= added;
            if (_persistedContactCount > 0 && !string.IsNullOrWhiteSpace(_archivePath) && !File.Exists(_archivePath))
            {
                _archiveDirty = true;
                _archiveHealthy = false;
            }
            if (_archiveDirty)
            {
                if (SaveArchive())
                {
                    _archiveDirty = false;
                    _persistedContactCount = _contacts.Count;
                    _archiveHealthy = !_archiveWasCorrupt;
                }
                else
                {
                    _archiveHealthy = false;
                }
            }
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

    private static List<DailyContactCount> DailyCounts(IEnumerable<StationHistoryContact> contacts, DateTime today, int days)
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

    private static List<HourlyContactCount> HourlyCounts(IEnumerable<StationHistoryContact> contacts)
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
        ArchivedContacts: _persistedContactCount,
        ArchiveStartedAt: _archiveStartedAt,
        ArchiveHealthy: _archiveHealthy,
        TrimodeFilesScanned: _trimodeFileIndex.Count,
        TrimodeFolderAvailable: _trimodeFolderAvailable,
        OldestTrimodeContact: _oldestTrimodeContact.HasValue ? ToOffset(_oldestTrimodeContact.Value) : null);

    private StationContactDto ToDto(StationHistoryContact contact) =>
        new(contact.Key, ToOffset(contact.LocalTime), contact.Station, contact.Source);

    private DateTimeOffset ToOffset(DateTime localTime) =>
        new(DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), _timeZone.GetUtcOffset(localTime));

    // MARK: - Cursor

    private static string EncodeCursor(StationHistoryContact contact) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(contact.Key));

    private static bool TryDecodeCursor(string? cursor, out StationHistoryContact contact)
    {
        contact = default;
        if (string.IsNullOrWhiteSpace(cursor)) return false;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|', 2);
            if (parts.Length != 2 ||
                !DateTime.TryParseExact(parts[0], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                return false;
            contact = new StationHistoryContact(time, parts[1], "Relay");
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

                var contacts = RelayStationLog.ParseLines(ReadSharedLines(file))
                    .Select(c => new StationHistoryContact(c.LocalTime, c.Station, "Relay")).ToList();
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

    private void IndexTrimodeFiles()
    {
        var folder = _trimodeLogFolder?.Invoke();
        _trimodeFolderAvailable = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
        if (!_trimodeFolderAvailable)
        {
            _trimodeFileIndex.Clear();
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(folder!, "*.adi", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(folder!, "*.adif", SearchOption.AllDirectories))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _trimodeFileIndex.Keys.Where(k => !present.Contains(k)).ToList())
            _trimodeFileIndex.Remove(stale);

        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (_trimodeFileIndex.TryGetValue(file, out var cached) &&
                    cached.WriteUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
                    continue;

                var contacts = TrimodeAdifLog.Parse(ReadSharedText(file), _timeZone).ToList();
                _trimodeFileIndex[file] = (info.LastWriteTimeUtc, info.Length, contacts);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Trimode may be writing this month's file; retry on the next pass.
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

    private static string ReadSharedText(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // MARK: - Archive

    private sealed record ArchiveFile(DateTimeOffset StartedAt, List<ArchivedContact> Contacts);

    private sealed record ArchivedContact(string Time, string Station, string Source = "Relay");

    private void LoadArchiveIfNeeded()
    {
        if (_archiveLoaded) return;
        _archiveLoaded = true;

        if (string.IsNullOrWhiteSpace(_archivePath))
            return;

        var path = _archivePath;
        _archiveWasCorrupt = File.Exists(path + ".corrupt");
        if (_archiveWasCorrupt)
            _archiveHealthy = false;
        ArchiveFile? payload = null;
        if (File.Exists(path))
        {
            payload = ReadArchive(path);
            if (payload is null)
            {
                _archiveWasCorrupt = true;
                _archiveHealthy = false;
                try
                {
                    // Keep the damaged original for manual recovery before replacing it.
                    File.Copy(path, path + ".corrupt", overwrite: false);
                }
                catch (IOException) when (File.Exists(path + ".corrupt"))
                {
                    // An earlier recovery already kept a copy.
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _archivePreservationFailed = true;
                }
            }
        }

        payload ??= ReadArchive(path + ".bak");
        if (payload is null) return;

        _archiveStartedAt = payload.StartedAt;
        foreach (var entry in payload.Contacts)
        {
            if (DateTime.TryParseExact(entry.Time, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) &&
                !string.IsNullOrWhiteSpace(entry.Station))
            {
                var source = entry.Source == "Trimode" ? "Trimode" : "Relay";
                var contact = new StationHistoryContact(time, entry.Station.ToUpperInvariant(), source);
                _archive.TryAdd(contact.Key, contact);
            }
        }
        _persistedContactCount = _archive.Count;
        if (_archiveWasCorrupt || !File.Exists(path))
            _archiveDirty = true;
    }

    private static ArchiveFile? ReadArchive(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<ArchiveFile>(File.ReadAllText(path), JsonOptions);
            return payload?.Contacts is null ? null : payload;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool SaveArchive()
    {
        if (string.IsNullOrWhiteSpace(_archivePath) || _archivePreservationFailed) return false;

        try
        {
            var startedAt = _archiveStartedAt ?? new DateTimeOffset(_localNow(), _timeZone.GetUtcOffset(_localNow()));
            var payload = new ArchiveFile(
                startedAt,
                _contacts
                    .Select(c => new ArchivedContact(c.LocalTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), c.Station, c.Source))
                    .ToList());

            var directory = Path.GetDirectoryName(_archivePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temporaryPath = _archivePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload, JsonOptions));
            if (!_archiveWasCorrupt && File.Exists(_archivePath))
                File.Copy(_archivePath, _archivePath + ".bak", overwrite: true);
            File.Move(temporaryPath, _archivePath, overwrite: true);
            _archiveStartedAt = startedAt;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the archive dirty and retry on the next collector pass.
            return false;
        }
    }
}
