using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Thread-safe device registry persisted at C:\PWM\MobilePushDevices.json (configurable).
/// Atomic writes; empty/missing file is OK.
/// </summary>
public sealed class MobileDeviceRegistry
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
    private readonly object _memoryLock = new();
    private MobileDeviceRegistryDocument? _cache;
    private string? _cachePath;

    public MobileDeviceRegistry(IOptionsMonitor<ApplePushOptions> options)
    {
        _options = options;
    }

    public string RegistryPath =>
        string.IsNullOrWhiteSpace(_options.CurrentValue.DeviceRegistryPath)
            ? @"C:\PWM\MobilePushDevices.json"
            : _options.CurrentValue.DeviceRegistryPath;

    /// <summary>Soft max devices (25–100). Default 50.</summary>
    public int MaxDevices => ResolveMaxDevices(_options.CurrentValue);

    public IReadOnlyList<MobileDeviceRecord> GetAll()
    {
        lock (_memoryLock)
        {
            return LoadUnlocked().Devices.Select(Clone).ToList();
        }
    }

    public MobileDeviceRecord? Get(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        lock (_memoryLock)
        {
            var match = LoadUnlocked().Devices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            return match is null ? null : Clone(match);
        }
    }

    public MobileDeviceRecord Register(MobileDeviceRegisterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DeviceId))
            throw new ArgumentException("deviceId is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ApnsToken))
            throw new ArgumentException("apnsToken is required.", nameof(request));

        var now = DateTimeOffset.UtcNow;
        lock (_memoryLock)
        {
            var doc = LoadUnlocked();
            var existing = doc.Devices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, request.DeviceId, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                var maxDevices = MaxDevices;
                if (doc.Devices.Count >= maxDevices)
                    throw new MobileDeviceRegistryFullException(maxDevices);

                existing = new MobileDeviceRecord
                {
                    DeviceId = request.DeviceId.Trim(),
                    RegisteredAt = now,
                    Preferences = MobilePushPreferences.CreateDefaults()
                };
                doc.Devices.Add(existing);
            }

            existing.ApnsToken = request.ApnsToken.Trim();
            existing.Environment = ApnsEnvironments.Normalize(request.Environment);
            existing.Platform = string.IsNullOrWhiteSpace(request.Platform) ? "ios" : request.Platform.Trim();
            existing.AppVersion = string.IsNullOrWhiteSpace(request.AppVersion) ? existing.AppVersion : request.AppVersion.Trim();
            existing.DeviceName = string.IsNullOrWhiteSpace(request.DeviceName) ? existing.DeviceName : request.DeviceName.Trim();
            existing.TokenStale = false;
            existing.UpdatedAt = now;

            SaveUnlocked(doc);
            return Clone(existing);
        }
    }

    public bool Remove(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        lock (_memoryLock)
        {
            var doc = LoadUnlocked();
            var removed = doc.Devices.RemoveAll(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return false;
            SaveUnlocked(doc);
            return true;
        }
    }

    public MobileDeviceRecord? UpdatePreferences(string deviceId, MobilePushPreferences preferences)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        lock (_memoryLock)
        {
            var doc = LoadUnlocked();
            var existing = doc.Devices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                return null;

            existing.Preferences = MobilePushPreferences.Normalize(preferences);
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            SaveUnlocked(doc);
            return Clone(existing);
        }
    }

    /// <summary>Clear token / mark stale on APNs 410. Does not delete the device.</summary>
    public bool MarkTokenStale(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        lock (_memoryLock)
        {
            var doc = LoadUnlocked();
            var existing = doc.Devices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                return false;

            existing.TokenStale = true;
            existing.ApnsToken = "";
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            SaveUnlocked(doc);
            return true;
        }
    }

    private MobileDeviceRegistryDocument LoadUnlocked()
    {
        var path = Path.GetFullPath(RegistryPath);
        if (_cache is not null && string.Equals(_cachePath, path, StringComparison.OrdinalIgnoreCase))
            return _cache;

        if (!File.Exists(path))
        {
            _cache = new MobileDeviceRegistryDocument();
            _cachePath = path;
            return _cache;
        }

        try
        {
            var json = File.ReadAllText(path);
            _cache = JsonSerializer.Deserialize<MobileDeviceRegistryDocument>(json, JsonOptions)
                     ?? new MobileDeviceRegistryDocument();
            _cache.Devices ??= [];
            foreach (var device in _cache.Devices)
                device.Preferences = MobilePushPreferences.Normalize(device.Preferences);
        }
        catch
        {
            _cache = new MobileDeviceRegistryDocument();
        }

        _cachePath = path;
        return _cache;
    }

    private void SaveUnlocked(MobileDeviceRegistryDocument document)
    {
        var path = Path.GetFullPath(RegistryPath);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Device registry path must include a directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var pathLock = PathLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        pathLock.Wait();
        try
        {
            var json = JsonSerializer.Serialize(document, JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
            _cache = document;
            _cachePath = path;
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

    private static MobileDeviceRecord Clone(MobileDeviceRecord source)
    {
        var json = JsonSerializer.Serialize(source, JsonOptions);
        return JsonSerializer.Deserialize<MobileDeviceRecord>(json, JsonOptions)
               ?? new MobileDeviceRecord();
    }

    private static int ResolveMaxDevices(ApplePushOptions options)
    {
        var max = options.MaxDevices > 0 ? options.MaxDevices : 50;
        return Math.Clamp(max, 25, 100);
    }
}
