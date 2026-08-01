using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GatewayPulse.RfMonitoring;

public static class RfTelemetryJson
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(RfTelemetry telemetry) =>
        JsonSerializer.Serialize(telemetry, Options);

    public static RfTelemetry? Deserialize(string json) =>
        JsonSerializer.Deserialize<RfTelemetry>(json, Options);

    public static async Task WriteFileAtomicallyAsync(
        string path,
        RfTelemetry telemetry,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The telemetry path must include a directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{fullPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var pathLock = PathLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));

        await pathLock.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(temporaryPath, Serialize(telemetry), cancellationToken);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            pathLock.Release();
        }
    }
}
