using System.Collections.Concurrent;
using System.Buffers.Binary;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace GatewayPulse.VictronMonitor.Bluetooth;

public sealed record BleScanRecord(
    DateTimeOffset Timestamp,
    string DeviceName,
    string Address,
    short Rssi,
    IReadOnlyDictionary<string, string> ManufacturerData,
    IReadOnlyDictionary<string, string> AdvertisementData,
    IReadOnlyList<Guid> ServiceUuids,
    bool? Connectable,
    string RawAdvertisementBytes);

public sealed class WindowsBleAdvertisementSource : IVictronAdvertisementSource, IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lifecycleSync = new();
    private bool _started;
    private bool _disposed;
    private int _restartPending;

    public WindowsBleAdvertisementSource()
    {
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
            AllowExtendedAdvertisements = true
        };
        _watcher.Received += OnReceived;
        _watcher.Stopped += OnStopped;
    }

    public event EventHandler<VictronAdvertisement>? AdvertisementReceived;
    public event EventHandler<BleScanRecord>? ScanRecordReceived;
    public event EventHandler<string>? StatusChanged;

    public Task StartAsync()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started)
            {
                _started = true;
                try
                {
                    _watcher.Start();
                }
                catch
                {
                    _started = false;
                    throw;
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_lifecycleSync)
        {
            if (_started)
            {
                _started = false;
                _watcher.Stop();
            }
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_lifecycleSync)
        {
            _disposed = true;
            if (_started)
            {
                _started = false;
                _watcher.Stop();
            }
            _watcher.Received -= OnReceived;
            _watcher.Stopped -= OnStopped;
        }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var address = FormatAddress(args.BluetoothAddress);
        var advertisedName = args.Advertisement.LocalName;
        if (!string.IsNullOrWhiteSpace(advertisedName))
            _names[address] = advertisedName;
        var knownName = _names.TryGetValue(address, out var cachedName) ? cachedName : null;
        var displayName = knownName ?? "Unknown BLE device";

        var manufacturerData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        byte[]? victronData = null;
        foreach (var item in args.Advertisement.ManufacturerData)
        {
            var bytes = ReadBuffer(item.Data);
            var loggedBytes = RedactManufacturerDataForLogging(item.CompanyId, bytes);
            AddUniqueData(manufacturerData, $"0x{item.CompanyId:X4}", Convert.ToHexString(loggedBytes));
            if (item.CompanyId == Protocol.VictronInstantReadoutDecoder.VictronCompanyId &&
                bytes.Length > 0 && bytes[0] == Protocol.VictronInstantReadoutDecoder.InstantReadoutRecordType)
            {
                victronData = bytes;
            }
        }

        var advertisementData = new Dictionary<string, string>();
        var raw = new List<byte>();
        foreach (var section in args.Advertisement.DataSections)
        {
            var bytes = ReadBuffer(section.Data);
            var loggedBytes = RedactAdvertisementSectionForLogging(section.DataType, bytes);
            AddUniqueData(advertisementData, $"0x{section.DataType:X2}", Convert.ToHexString(loggedBytes));
            raw.Add(checked((byte)(loggedBytes.Length + 1)));
            raw.Add(section.DataType);
            raw.AddRange(loggedBytes);
        }

        var serviceUuids = args.Advertisement.ServiceUuids.ToArray();
        bool? connectable = args.AdvertisementType switch
        {
            BluetoothLEAdvertisementType.ConnectableDirected => true,
            BluetoothLEAdvertisementType.ConnectableUndirected => true,
            BluetoothLEAdvertisementType.NonConnectableUndirected => false,
            BluetoothLEAdvertisementType.ScannableUndirected => false,
            _ => null
        };

        ScanRecordReceived?.Invoke(this, new BleScanRecord(
            DateTimeOffset.UtcNow,
            displayName,
            address,
            args.RawSignalStrengthInDBm,
            manufacturerData,
            advertisementData,
            serviceUuids,
            connectable,
            Convert.ToHexString(raw.ToArray())));

        if (victronData is not null)
        {
            AdvertisementReceived?.Invoke(this, new VictronAdvertisement(
                address,
                knownName,
                args.RawSignalStrengthInDBm,
                victronData,
                serviceUuids,
                connectable,
                raw.ToArray()));
        }
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        StatusChanged?.Invoke(this, $"BLE watcher stopped: {args.Error}.");
        lock (_lifecycleSync)
        {
            if (!_started || _disposed || args.Error == BluetoothError.Success || Interlocked.Exchange(ref _restartPending, 1) != 0)
                return;
        }

        _ = RestartAfterFailureAsync();
    }

    private async Task RestartAfterFailureAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            var restarted = false;
            lock (_lifecycleSync)
            {
                if (_started && !_disposed)
                {
                    _watcher.Start();
                    restarted = true;
                }
            }
            if (restarted)
                StatusChanged?.Invoke(this, "BLE watcher restarted after a communication failure.");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"BLE watcher restart failed: {ex.Message}");
            var shouldRetry = false;
            lock (_lifecycleSync)
                shouldRetry = _started && !_disposed;
            if (shouldRetry)
            {
                Interlocked.Exchange(ref _restartPending, 0);
                _ = RestartAfterFailureAsync();
                return;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _restartPending, 0);
        }
    }

    private static void AddUniqueData(IDictionary<string, string> target, string baseKey, string value)
    {
        var key = baseKey;
        for (var occurrence = 2; target.ContainsKey(key); occurrence++)
            key = $"{baseKey}#{occurrence}";
        target[key] = value;
    }

    public static byte[] RedactManufacturerDataForLogging(ushort companyId, ReadOnlySpan<byte> data)
    {
        var logged = data.ToArray();
        if (companyId == Protocol.VictronInstantReadoutDecoder.VictronCompanyId &&
            logged.Length > 7 && logged[0] == Protocol.VictronInstantReadoutDecoder.InstantReadoutRecordType)
        {
            logged[7] = 0;
        }
        return logged;
    }

    public static byte[] RedactAdvertisementSectionForLogging(byte dataType, ReadOnlySpan<byte> data)
    {
        var logged = data.ToArray();
        if (dataType == 0xFF && logged.Length > 9 &&
            BinaryPrimitives.ReadUInt16LittleEndian(logged) == Protocol.VictronInstantReadoutDecoder.VictronCompanyId &&
            logged[2] == Protocol.VictronInstantReadoutDecoder.InstantReadoutRecordType)
        {
            logged[9] = 0;
        }
        return logged;
    }

    private static byte[] ReadBuffer(IBuffer buffer)
    {
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    internal static string FormatAddress(ulong bluetoothAddress)
    {
        var hex = bluetoothAddress.ToString("X12");
        return string.Join(':', Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2)));
    }
}
