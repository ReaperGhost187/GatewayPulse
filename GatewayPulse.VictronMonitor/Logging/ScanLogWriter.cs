using System.Text.Json;

namespace GatewayPulse.VictronMonitor.Logging;

public sealed class ScanLogWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ScanLogWriter(string logsPath)
    {
        Directory.CreateDirectory(logsPath);
        Path = System.IO.Path.Combine(
            logsPath,
            $"scan-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl");
        _writer = new StreamWriter(new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public string Path { get; }

    public void Write(Bluetooth.BleScanRecord record)
    {
        var json = JsonSerializer.Serialize(record, _jsonOptions);
        lock (_sync)
            _writer.WriteLine(json);
    }

    public void Dispose()
    {
        lock (_sync)
            _writer.Dispose();
    }
}
