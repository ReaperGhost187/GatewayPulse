using System.Text.Json;

namespace GatewayPulse.RfMonitoring;

public sealed class RfTransmissionHistoryStore
{
    public const int DefaultMaxEvents = 500;

    private readonly string _path;
    private readonly int _maxEvents;
    private readonly object _gate = new();
    private List<RfTransmissionEvent> _events = [];
    private bool _loaded;

    public RfTransmissionHistoryStore(string path, int maxEvents = DefaultMaxEvents)
    {
        _path = Path.GetFullPath(path);
        _maxEvents = Math.Clamp(maxEvents, 50, 5000);
    }

    public void Add(RfTransmissionEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        lock (_gate)
        {
            EnsureLoaded_NoLock();
            _events.Insert(0, completed);
            if (_events.Count > _maxEvents)
                _events.RemoveRange(_maxEvents, _events.Count - _maxEvents);
            Persist_NoLock();
        }
    }

    public IReadOnlyList<RfTransmissionEvent> List(int take = 50)
    {
        lock (_gate)
        {
            EnsureLoaded_NoLock();
            return _events.Take(Math.Clamp(take, 1, _maxEvents)).ToList();
        }
    }

    private void EnsureLoaded_NoLock()
    {
        if (_loaded)
            return;
        _loaded = true;
        if (!File.Exists(_path))
            return;
        try
        {
            var json = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize<RfTransmissionHistoryFile>(json, RfTelemetryJson.Options);
            _events = file?.Events ?? [];
        }
        catch
        {
            _events = [];
        }
    }

    private void Persist_NoLock()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var temporaryPath = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var payload = new RfTransmissionHistoryFile { Events = _events };
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload, RfTelemetryJson.Options));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private sealed class RfTransmissionHistoryFile
    {
        public List<RfTransmissionEvent> Events { get; set; } = [];
    }
}
