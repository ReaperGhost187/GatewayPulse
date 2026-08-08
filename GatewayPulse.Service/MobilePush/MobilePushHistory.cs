using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

public sealed class MobilePushHistoryEntry
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public int DeviceCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    /// <summary>Device IDs only — never APNs tokens.</summary>
    public List<string> DeviceIds { get; set; } = [];
}

public sealed class MobilePushHistoryDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<MobilePushHistoryEntry> Entries { get; set; } = [];
}

/// <summary>Bounded push history (100–500). Never stores APNs tokens.</summary>
public sealed class MobilePushHistory
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IOptionsMonitor<ApplePushOptions> _options;
    private readonly object _lock = new();

    public MobilePushHistory(IOptionsMonitor<ApplePushOptions> options)
    {
        _options = options;
    }

    public IReadOnlyList<MobilePushHistoryEntry> GetRecent(int take = 50)
    {
        lock (_lock)
        {
            var doc = LoadUnlocked();
            return doc.Entries.Take(Math.Clamp(take, 1, 500)).Select(Clone).ToList();
        }
    }

    public void Record(MobilePushHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            var doc = LoadUnlocked();
            doc.Entries.Insert(0, entry);
            var capacity = Math.Clamp(_options.CurrentValue.HistoryCapacity, 100, 500);
            if (doc.Entries.Count > capacity)
                doc.Entries.RemoveRange(capacity, doc.Entries.Count - capacity);
            SaveUnlocked(doc);
        }
    }

    private string HistoryPath =>
        string.IsNullOrWhiteSpace(_options.CurrentValue.HistoryPath)
            ? @"C:\PWM\MobilePushHistory.json"
            : _options.CurrentValue.HistoryPath;

    private MobilePushHistoryDocument LoadUnlocked()
    {
        var path = Path.GetFullPath(HistoryPath);
        if (!File.Exists(path))
            return new MobilePushHistoryDocument();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MobilePushHistoryDocument>(json, JsonOptions)
                   ?? new MobilePushHistoryDocument();
        }
        catch
        {
            return new MobilePushHistoryDocument();
        }
    }

    private void SaveUnlocked(MobilePushHistoryDocument document)
    {
        var path = Path.GetFullPath(HistoryPath);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("History path must include a directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var pathLock = PathLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        pathLock.Wait();
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { /* best effort */ }
            }
            pathLock.Release();
        }
    }

    private static MobilePushHistoryEntry Clone(MobilePushHistoryEntry source)
    {
        var json = JsonSerializer.Serialize(source, JsonOptions);
        return JsonSerializer.Deserialize<MobilePushHistoryEntry>(json, JsonOptions)
               ?? new MobilePushHistoryEntry();
    }
}
