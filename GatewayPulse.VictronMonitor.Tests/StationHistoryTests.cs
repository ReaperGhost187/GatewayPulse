using GatewayPulse.Core;
using GatewayPulse.ServiceHosting;
using Microsoft.AspNetCore.Http;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class StationHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gp-station-history-" + Guid.NewGuid().ToString("N"));
    private readonly string _logs;
    private readonly string _trimodeLogs;
    private readonly string _archive;
    private static readonly DateTime Now = new(2026, 8, 29, 20, 0, 0);

    public StationHistoryTests()
    {
        _logs = Path.Combine(_root, "Logs");
        _trimodeLogs = Path.Combine(_root, "TrimodeLogs");
        _archive = Path.Combine(_root, "data", "StationContactHistory.json");
        Directory.CreateDirectory(_logs);
        Directory.CreateDirectory(_trimodeLogs);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string Connection(string timestamp, string station) =>
        $"{timestamp} HF client connection from {station}";

    private void WriteLog(string name, DateTime lastWriteUtc, params string[] lines)
    {
        var path = Path.Combine(_logs, name);
        File.WriteAllLines(path, lines);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
    }

    private StationHistoryStore CreateStore() =>
        new(() => _logs, _archive, () => Now, TimeZoneInfo.Utc, TimeSpan.Zero, () => _trimodeLogs);

    private static string Adif(string station, string date, string time) =>
        $"<CALL:{station.Length}>{station}<QSO_DATE:8>{date}<TIME_ON:{time.Length}>{time}<MODE:4>PACT<EOR>";

    [Fact]
    public void ParsesTrimodeAdifSessionsAcrossYears()
    {
        var text = "Trimode export <EOH>" +
            Adif("NNX1AA-5", "20200403", "111142") + "\n" +
            Adif("NNX2BB", "20260829", "1805") + "\n" +
            "<CALL:3>bad<QSO_DATE:8>nonsense<TIME_ON:6>111142<EOR>";

        var contacts = TrimodeAdifLog.Parse(text).ToList();

        Assert.Equal(2, contacts.Count);
        Assert.Equal("NNX1AA-5", contacts[0].Station);
        Assert.Equal(new DateTime(2020, 4, 3, 11, 11, 42), contacts[0].LocalTime);
        Assert.Equal("Trimode", contacts[0].Source);
        Assert.Equal(new DateTime(2026, 8, 29, 18, 5, 0), contacts[1].LocalTime);
    }

    [Fact]
    public void CombinesRelayAndTrimodeWithoutDoubleCountingOverlap()
    {
        WriteLog("Events.log", Now, Connection("2026/08/29 18:21:26", "NNX1AA"));
        File.WriteAllText(Path.Combine(_trimodeLogs, "RMS Trimode_ADIF_202608.adi"),
            "<EOH>" + Adif("NNX1AA", "20260829", "182125") +
            Adif("NNX2BB", "20200403", "111142"));

        var store = CreateStore();
        var summary = store.GetSummary(90);
        var page = store.GetContacts(null, null, 50);

        Assert.Equal(2, summary.TotalContacts);
        Assert.Equal(new DateTimeOffset(2020, 4, 3, 11, 11, 42, TimeSpan.Zero), summary.Coverage.OldestContact);
        Assert.Equal(1, summary.Coverage.TrimodeFilesScanned);
        Assert.True(summary.Coverage.TrimodeFolderAvailable);
        Assert.Equal(new[] { "Relay", "Trimode" }, page.Items.Select(c => c.Source));

        File.Delete(Path.Combine(_trimodeLogs, "RMS Trimode_ADIF_202608.adi"));
        Assert.Equal(2, CreateStore().GetSummary(90).TotalContacts);
    }

    [Fact]
    public void ExistingRelayOnlyArchiveLoadsWithSourcePreserved()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_archive)!);
        File.WriteAllText(_archive,
            "{\"StartedAt\":\"2026-08-29T20:00:00+00:00\",\"Contacts\":[{\"Time\":\"2026-08-29 18:21:26\",\"Station\":\"NNX1AA\"}]}");

        var page = CreateStore().GetContacts(null, null, 50);
        Assert.Equal("Relay", Assert.Single(page.Items).Source);
    }

    // MARK: Parsing

    [Theory]
    [InlineData("2026/08/29 18:21:26 HF client connection from NNX1AA", "NNX1AA", 18, 21, 26)]
    [InlineData("2026-08-29 07:05:00 *** HF client connection from nnx2bb on 7102.0", "NNX2BB", 7, 5, 0)]
    public void ParsesRelayConnectionLines(string line, string station, int hour, int minute, int second)
    {
        Assert.True(RelayStationLog.TryParseContact(line, out var contact));
        Assert.Equal(station, contact.Station);
        Assert.Equal(new DateTime(2026, 8, 29, hour, minute, second), contact.LocalTime);
    }

    [Theory]
    [InlineData("HF client connection from NNX1AA")]
    [InlineData("2026/08/29 18:21:26 RMS Relay started")]
    [InlineData("2026/08/29 18:21:26 Telnet client connection from NNX1AA")]
    [InlineData("")]
    public void IgnoresNonContactLines(string line)
    {
        Assert.False(RelayStationLog.TryParseContact(line, out _));
    }

    // MARK: Status de-duplication and latest station

    [Fact]
    public void StatusCountsEachContactOnceWhenTheSameLineIsInTwoFiles()
    {
        // Mirrors the saved status.json: every contact appeared in two Relay log files.
        var lines = new[]
        {
            Connection("2026/08/29 18:21:26", "NNX1AA"),
            Connection("2026/08/29 18:11:05", "NNX1AA"),
            Connection("2026/08/29 09:00:00", "NNX2BB")
        };
        var bothFiles = lines.Concat(lines);

        var status = new GatewayStatus();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var connections = new List<StationConnection>();
        var hourly = Enumerable.Range(0, 24).Select(h => new HourlyActivity { Hour = $"{h:00}:00" }).ToList();

        GatewayPulseService.ApplyRelayLogLines(bothFiles, status, new List<GatewayEvent>(), counts, connections, hourly);

        Assert.Equal(3, connections.Count);
        Assert.Equal(2, counts["NNX1AA"]);
        Assert.Equal(1, counts["NNX2BB"]);
    }

    [Fact]
    public void LastStationIsTheNewestContactRegardlessOfReadOrder()
    {
        // Files are read newest-first, so the oldest contact is the last line processed.
        var lines = new[]
        {
            Connection("2026/08/29 18:21:26", "NNX1AA"),
            Connection("2026/08/28 23:59:00", "NNX2BB")
        };

        var status = new GatewayStatus();
        GatewayPulseService.ApplyRelayLogLines(
            lines, status, new List<GatewayEvent>(), new Dictionary<string, int>(), new List<StationConnection>(),
            Enumerable.Range(0, 24).Select(h => new HourlyActivity()).ToList());

        Assert.Equal("NNX1AA", status.LastStation);
        Assert.Equal("2026/08/29 18:21:26", status.LastRelayEvent);
    }

    // MARK: Replay of a real gateway snapshot

    /// The 21 unique Relay contacts behind a saved production /api/status (callsigns anonymized).
    /// That status reported 42 connection rows, counts of 40 and 2, and the oldest station as lastStation.
    private static readonly (string Timestamp, string Station)[] SnapshotContacts =
    {
        ("2026/08/29 18:21:26", "NNX1AA"),
        ("2026/08/29 18:11:05", "NNX1AA"),
        ("2026/08/29 18:00:34", "NNX1AA"),
        ("2026/08/29 17:50:50", "NNX1AA"),
        ("2026/08/29 17:41:18", "NNX1AA"),
        ("2026/08/29 17:31:36", "NNX1AA"),
        ("2026/08/29 17:25:14", "NNX1AA"),
        ("2026/08/29 17:01:51", "NNX1AA"),
        ("2026/08/29 16:55:34", "NNX1AA"),
        ("2026/08/29 16:51:57", "NNX1AA"),
        ("2026/08/29 16:40:38", "NNX1AA"),
        ("2026/08/29 16:30:09", "NNX1AA"),
        ("2026/08/29 16:21:14", "NNX1AA"),
        ("2026/08/29 16:16:19", "NNX1AA"),
        ("2026/08/29 16:13:43", "NNX1AA"),
        ("2026/08/29 16:12:13", "NNX1AA"),
        ("2026/08/29 16:09:36", "NNX1AA"),
        ("2026/08/29 16:06:47", "NNX1AA"),
        ("2026/08/29 16:02:29", "NNX1AA"),
        ("2026/08/29 15:54:51", "NNX1AA"),
        ("2026/08/19 19:09:56", "NNX2BB")
    };

    [Fact]
    public void SnapshotReplay_StatusCountsAndLastStationAreCorrect()
    {
        // Each contact appears in two Relay log files; files are read newest-first, so the oldest
        // contact is the last line processed (what produced the wrong lastStation).
        var lines = SnapshotContacts.Select(c => Connection(c.Timestamp, c.Station)).ToArray();
        var status = new GatewayStatus();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var connections = new List<StationConnection>();

        GatewayPulseService.ApplyRelayLogLines(
            lines.Concat(lines), status, new List<GatewayEvent>(), counts, connections,
            Enumerable.Range(0, 24).Select(h => new HourlyActivity()).ToList());

        Assert.Equal(21, connections.Count);
        Assert.Equal(20, counts["NNX1AA"]);
        Assert.Equal(1, counts["NNX2BB"]);
        Assert.Equal("NNX1AA", status.LastStation);
        Assert.Equal("2026/08/29 18:21:26", status.LastRelayEvent);
    }

    [Fact]
    public void SnapshotReplay_HistoryStoreMatches()
    {
        var lines = SnapshotContacts.Select(c => Connection(c.Timestamp, c.Station)).ToArray();
        WriteLog("RMS Relay Events.log", Now, lines);
        WriteLog("RMS Relay.log", Now.AddMinutes(-1), lines);

        var store = CreateStore();
        var summary = store.GetSummary(days: 30);

        Assert.Equal(21, summary.TotalContacts);
        Assert.Equal(2, summary.UniqueStations);
        Assert.Equal(new[] { ("NNX1AA", 20), ("NNX2BB", 1) }, summary.Stations.Select(s => (s.Station, s.Contacts)));
        Assert.Equal("NNX1AA", summary.LatestContact?.Station);
        Assert.Equal(21, store.GetContacts(null, null, 200).TotalMatching);
    }

    [Theory]
    [InlineData("2026/08/29 18:21:26 HF client connection from KX7ABC-5", "KX7ABC-5")]
    [InlineData("2026/08/29 18:21:26 HF client connection from KX7ABC -5", "KX7ABC")]
    public void KeepsSsidAsPartOfTheStation(string line, string station)
    {
        Assert.True(RelayStationLog.TryParseContact(line, out var contact));
        Assert.Equal(station, contact.Station);
    }

    [Fact]
    public void IndexesLogsInSubfolders()
    {
        WriteLog("Events.log", Now, Connection("2026/08/29 18:21:26", "NNX1AA"));
        var archived = Path.Combine(_logs, "Archive");
        Directory.CreateDirectory(archived);
        File.WriteAllLines(Path.Combine(archived, "Events 2026-07.log"), new[] { Connection("2026/07/01 06:00:00", "NNX2BB") });

        var summary = CreateStore().GetSummary(days: 7);

        Assert.Equal(2, summary.TotalContacts);
        Assert.Equal(2, summary.Coverage.LogFilesScanned);
    }

    [Fact]
    public void ChangedLogFileIsReindexed()
    {
        WriteLog("Events.log", Now.AddMinutes(-5), Connection("2026/08/29 18:11:05", "NNX1AA"));
        var store = CreateStore();
        Assert.Equal(1, store.GetSummary(7).TotalContacts);

        WriteLog("Events.log", Now,
            Connection("2026/08/29 18:11:05", "NNX1AA"),
            Connection("2026/08/29 18:21:26", "NNX2BB"));

        var summary = store.GetSummary(7);
        Assert.Equal(2, summary.TotalContacts);
        Assert.Equal("NNX2BB", summary.LatestContact?.Station);
    }

    [Fact]
    public async Task CollectorArchivesWithoutAnyApiRequest()
    {
        WriteLog("Events.log", Now, Connection("2026/08/29 18:21:26", "NNX1AA"));
        var store = CreateStore();
        var collector = new StationHistoryCollector(
            store,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StationHistoryCollector>.Instance,
            TimeSpan.FromMinutes(5));

        await collector.StartAsync(CancellationToken.None);
        for (var i = 0; i < 50 && !File.Exists(_archive); i++)
            await Task.Delay(20);
        await collector.StopAsync(CancellationToken.None);

        Assert.True(File.Exists(_archive));
        Assert.Contains("NNX1AA", File.ReadAllText(_archive));
    }

    // MARK: Store summary, ordering, counts

    [Fact]
    public void SummaryDeduplicatesAcrossFilesAndRanksStations()
    {
        WriteLog("Events 1.log", Now.AddDays(-1),
            Connection("2026/08/28 10:00:00", "NNX2BB"),
            Connection("2026/08/29 18:21:26", "NNX1AA"));
        WriteLog("RMS Relay 1.log", Now,
            Connection("2026/08/29 18:21:26", "NNX1AA"),
            Connection("2026/08/29 18:11:05", "NNX1AA"),
            "2026/08/29 18:00:00 RMS Relay started");

        var summary = CreateStore().GetSummary(days: 7);

        Assert.Equal(3, summary.TotalContacts);
        Assert.Equal(2, summary.UniqueStations);
        Assert.Equal(2, summary.ContactsToday);
        Assert.Equal("NNX1AA", summary.LatestContact?.Station);
        Assert.Equal(new[] { "NNX1AA", "NNX2BB" }, summary.Stations.Select(s => s.Station));
        Assert.Equal(2, summary.Stations[0].Contacts);
        Assert.Equal(new DateTimeOffset(2026, 8, 29, 18, 11, 5, TimeSpan.Zero), summary.Stations[0].FirstSeen);
        Assert.Equal(2, summary.Coverage.LogFilesScanned);
        Assert.True(summary.Coverage.LogFolderAvailable);
    }

    [Fact]
    public void DailySeriesIsZeroFilledAndHourlyCoversAllContacts()
    {
        WriteLog("Events.log", Now,
            Connection("2026/08/29 18:21:26", "NNX1AA"),
            Connection("2026/08/27 18:05:00", "NNX2BB"),
            Connection("2026/07/01 06:00:00", "NNX2BB")); // outside the 7-day window

        var summary = CreateStore().GetSummary(days: 7);

        Assert.Equal(7, summary.Daily.Count);
        Assert.Equal("2026-08-23", summary.Daily[0].Date);
        Assert.Equal("2026-08-29", summary.Daily[^1].Date);
        Assert.Equal(1, summary.Daily[^1].Contacts);
        Assert.Equal(1, summary.Daily[^3].Contacts);
        Assert.Equal(2, summary.Daily.Sum(d => d.Contacts));

        Assert.Equal(24, summary.Hourly.Count);
        Assert.Equal(2, summary.Hourly[18].Contacts);
        Assert.Equal(1, summary.Hourly[6].Contacts);
    }

    [Fact]
    public void StationDetailReportsRankAndTotals()
    {
        WriteLog("Events.log", Now,
            Connection("2026/08/29 18:21:26", "NNX1AA"),
            Connection("2026/08/29 18:11:05", "NNX1AA"),
            Connection("2026/08/29 09:00:00", "NNX2BB"));

        var store = CreateStore();
        var detail = store.GetStation("nnx2bb", days: 30);

        Assert.NotNull(detail);
        Assert.Equal("NNX2BB", detail!.Station);
        Assert.Equal(1, detail.Contacts);
        Assert.Equal(2, detail.Rank);
        Assert.Equal(2, detail.TotalStations);
        Assert.Null(store.GetStation("K0XYZ", days: 30));
    }

    // MARK: Pagination

    [Fact]
    public void PagesAreNewestFirstWithoutGapsOrDuplicates()
    {
        var lines = Enumerable.Range(0, 25)
            .Select(i => Connection(Now.AddMinutes(-i * 7).ToString("yyyy/MM/dd HH:mm:ss"), i % 3 == 0 ? "NNX1AA" : "NNX2BB"))
            .ToArray();
        WriteLog("Events.log", Now, lines);
        var store = CreateStore();

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = store.GetContacts(station: null, cursor, limit: 10);
            Assert.Equal(25, page.TotalMatching);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(25, seen.Distinct().Count());
        var times = store.GetContacts(null, null, 200).Items.Select(i => i.Timestamp).ToList();
        Assert.Equal(times.OrderByDescending(t => t), times);
    }

    [Fact]
    public void ContactsCanBeFilteredByStation()
    {
        WriteLog("Events.log", Now,
            Connection("2026/08/29 18:21:26", "NNX1AA"),
            Connection("2026/08/29 18:11:05", "NNX1AA"),
            Connection("2026/08/29 09:00:00", "NNX2BB"));

        var page = CreateStore().GetContacts("nnx1aa", null, 50);

        Assert.Equal(2, page.TotalMatching);
        Assert.All(page.Items, item => Assert.Equal("NNX1AA", item.Station));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void InvalidCursorStartsFromTheNewestContact()
    {
        WriteLog("Events.log", Now, Connection("2026/08/29 18:21:26", "NNX1AA"));

        var page = CreateStore().GetContacts(null, "not-a-cursor", 10);

        Assert.Single(page.Items);
    }

    // MARK: Archive and coverage

    [Fact]
    public void ArchiveKeepsContactsAfterRelayPrunesOldLogs()
    {
        WriteLog("Events old.log", Now.AddDays(-30), Connection("2026/07/30 12:00:00", "NNX2BB"));
        WriteLog("Events new.log", Now, Connection("2026/08/29 18:21:26", "NNX1AA"));
        CreateStore().Refresh(force: true);

        File.Delete(Path.Combine(_logs, "Events old.log"));
        var summary = CreateStore().GetSummary(days: 30);

        Assert.Equal(2, summary.TotalContacts);
        Assert.Equal(2, summary.Coverage.ArchivedContacts);
        Assert.Equal(new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero), summary.Coverage.OldestContact);
        Assert.Equal(new DateTimeOffset(2026, 8, 29, 18, 21, 26, TimeSpan.Zero), summary.Coverage.OldestLogContact);
        Assert.NotNull(summary.Coverage.ArchiveStartedAt);
    }

    [Fact]
    public void MissingLogFolderReportsNoCoverage()
    {
        var store = new StationHistoryStore(() => Path.Combine(_root, "missing"), null, () => Now, TimeZoneInfo.Utc, TimeSpan.Zero);

        var summary = store.GetSummary(days: 30);

        Assert.Equal(0, summary.TotalContacts);
        Assert.False(summary.Coverage.LogFolderAvailable);
        Assert.Null(summary.Coverage.OldestContact);
        Assert.Null(summary.LatestContact);
    }

    [Fact]
    public void CorruptArchiveIsIgnoredAndRebuiltFromLogs()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_archive)!);
        File.WriteAllText(_archive, "{ not json");
        WriteLog("Events.log", Now, Connection("2026/08/29 18:21:26", "NNX1AA"));

        var summary = CreateStore().GetSummary(days: 7);

        Assert.Equal(1, summary.TotalContacts);
        Assert.Contains("NNX1AA", File.ReadAllText(_archive));
        Assert.False(summary.Coverage.ArchiveHealthy);
        Assert.True(File.Exists(_archive + ".corrupt"));
    }

    [Fact]
    public void ArchiveWriteFailureRetriesWithoutAnotherContact()
    {
        WriteLog("Events.log", Now, Connection("2026/08/29 18:21:26", "NNX1AA"));
        Directory.CreateDirectory(Path.GetDirectoryName(_archive)!);
        Directory.CreateDirectory(_archive); // A directory at the file path makes replacement fail.

        var store = CreateStore();
        store.Refresh(force: true);
        Assert.False(store.GetCoverage().ArchiveHealthy);
        Assert.Equal(0, store.GetCoverage().ArchivedContacts);

        Directory.Delete(_archive);
        store.Refresh(force: true);
        Assert.True(store.GetCoverage().ArchiveHealthy);
        Assert.Equal(1, store.GetCoverage().ArchivedContacts);

        File.Delete(Path.Combine(_logs, "Events.log"));
        Assert.Equal(1, CreateStore().GetSummary(7).TotalContacts);
    }

    [Fact]
    public void DamagedPrimaryRecoversFromBackupAndKeepsOriginalForReview()
    {
        WriteLog("Events.log", Now, Connection("2026/08/29 18:11:05", "NNX1AA"));
        var store = CreateStore();
        store.Refresh(force: true);
        WriteLog("Events.log", Now.AddMinutes(1),
            Connection("2026/08/29 18:11:05", "NNX1AA"),
            Connection("2026/08/29 18:21:26", "NNX2BB"));
        store.Refresh(force: true);
        Assert.True(File.Exists(_archive + ".bak"));

        File.WriteAllText(_archive, "{ damaged json");
        File.Delete(Path.Combine(_logs, "Events.log"));
        var recovered = CreateStore().GetSummary(7);

        Assert.Equal(1, recovered.TotalContacts);
        Assert.Equal("NNX1AA", recovered.LatestContact?.Station);
        Assert.False(recovered.Coverage.ArchiveHealthy);
        Assert.Contains("damaged json", File.ReadAllText(_archive + ".corrupt"));
    }

    [Fact]
    public void StationEndpointsRequireMobileAuthForGet()
    {
        Assert.True(MobileApiAuthMiddleware.RequiresAuth(HttpMethods.Get, "/api/stations"));
        Assert.True(MobileApiAuthMiddleware.RequiresAuth(HttpMethods.Get, "/api/stations/contacts"));
        Assert.True(MobileApiAuthMiddleware.RequiresAuth(HttpMethods.Get, "/api/stations/NNX1AA"));
    }
}
