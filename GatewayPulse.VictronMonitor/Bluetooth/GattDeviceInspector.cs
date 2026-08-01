using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;
using GatewayPulse.VictronMonitor.Logging;

namespace GatewayPulse.VictronMonitor.Bluetooth;

public static class GattDeviceInspector
{
    public static async Task RunReconnectLoopAsync(
        string address,
        MonitorLogger logger,
        TimeSpan reconnectInterval,
        CancellationToken cancellationToken)
    {
        var bluetoothAddress = ParseAddress(address);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await InspectAndListenAsync(bluetoothAddress, logger, reconnectInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.Error($"GATT session error for {address}: {ex.Message}");
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                logger.Warning($"GATT connection ended; retrying {address} in {reconnectInterval.TotalSeconds:0.#} seconds.");
                await Task.Delay(reconnectInterval, cancellationToken);
            }
        }
    }

    private static async Task InspectAndListenAsync(
        ulong bluetoothAddress,
        MonitorLogger logger,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        logger.Info($"Opening BLE GATT device {FormatAddress(bluetoothAddress)}.");
        using var device = await BluetoothLEDevice
            .FromBluetoothAddressAsync(bluetoothAddress)
            .AsTask(cancellationToken);
        if (device is null)
            throw new IOException("Windows could not open the Bluetooth LE device. Confirm that it is advertising and in range.");

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        logger.Info($"GATT device opened: name='{device.Name}', status={device.ConnectionStatus}, id='{device.DeviceId}'.");
        device.ConnectionStatusChanged += OnConnectionStatusChanged;
        var services = new List<GattDeviceService>();
        var readableCharacteristics = new List<GattCharacteristic>();
        var subscriptions = new List<(
            GattCharacteristic Characteristic,
            TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> Handler,
            bool Configured)>();
        try
        {
            var servicesResult = await device
                .GetGattServicesAsync(BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken);
            logger.Info($"GATT service discovery: {servicesResult.Status}, count={servicesResult.Services.Count}.");
            if (servicesResult.Status != GattCommunicationStatus.Success)
                throw new IOException($"GATT service discovery failed: {servicesResult.Status}.");
            services.AddRange(servicesResult.Services);

            foreach (var service in services)
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger.Info($"GATT service {service.Uuid}.");
                var characteristicsResult = await service
                    .GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
                    .AsTask(cancellationToken);
                logger.Info($"  Characteristic discovery: {characteristicsResult.Status}, count={characteristicsResult.Characteristics.Count}.");
                if (RequiresReconnect(characteristicsResult.Status))
                    throw new IOException($"GATT characteristic discovery failed for service {service.Uuid}: {characteristicsResult.Status}.");

                foreach (var characteristic in characteristicsResult.Characteristics)
                {
                    var properties = characteristic.CharacteristicProperties;
                    logger.Info($"  Characteristic {characteristic.Uuid}, properties={properties}.");

                    var descriptorsResult = await characteristic
                        .GetDescriptorsAsync(BluetoothCacheMode.Uncached)
                        .AsTask(cancellationToken);
                    logger.Info($"    Descriptors: status={descriptorsResult.Status}, [{string.Join(", ", descriptorsResult.Descriptors.Select(item => item.Uuid))}].");

                    if (properties.HasFlag(GattCharacteristicProperties.Read))
                        readableCharacteristics.Add(characteristic);

                    var notifyMode = properties.HasFlag(GattCharacteristicProperties.Notify)
                        ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                        : properties.HasFlag(GattCharacteristicProperties.Indicate)
                            ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                            : GattClientCharacteristicConfigurationDescriptorValue.None;

                    if (notifyMode != GattClientCharacteristicConfigurationDescriptorValue.None)
                    {
                        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler = (_, eventArgs) =>
                            logger.Info($"    Notification {characteristic.Uuid}: {Convert.ToHexString(ReadBuffer(eventArgs.CharacteristicValue))}.");
                        characteristic.ValueChanged += handler;
                        var subscriptionIndex = subscriptions.Count;
                        subscriptions.Add((characteristic, handler, false));
                        var status = await characteristic
                            .WriteClientCharacteristicConfigurationDescriptorAsync(notifyMode)
                            .AsTask(cancellationToken);
                        subscriptions[subscriptionIndex] = (characteristic, handler, status == GattCommunicationStatus.Success);
                        logger.Info($"    Subscribe {notifyMode}: {status}.");
                    }
                }
            }

            do
            {
                foreach (var characteristic in readableCharacteristics)
                {
                    var readResult = await characteristic
                        .ReadValueAsync(BluetoothCacheMode.Uncached)
                        .AsTask(cancellationToken);
                    var value = readResult.Status == GattCommunicationStatus.Success
                        ? Convert.ToHexString(ReadBuffer(readResult.Value))
                        : "";
                    logger.Info($"    Poll {characteristic.Uuid}: status={readResult.Status}, value={value}.");
                }

                var pollDelay = Task.Delay(pollInterval, cancellationToken);
                await Task.WhenAny(pollDelay, disconnected.Task);
                cancellationToken.ThrowIfCancellationRequested();
            }
            while (device.ConnectionStatus == BluetoothConnectionStatus.Connected && !disconnected.Task.IsCompleted);
        }
        finally
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Characteristic.ValueChanged -= subscription.Handler;
                if (subscription.Configured)
                {
                    try
                    {
                        await subscription.Characteristic
                            .WriteClientCharacteristicConfigurationDescriptorAsync(
                                GattClientCharacteristicConfigurationDescriptorValue.None)
                            .AsTask()
                            .WaitAsync(TimeSpan.FromSeconds(1));
                    }
                    catch (Exception ex)
                    {
                        logger.Warning($"Unable to disable notification {subscription.Characteristic.Uuid}: {ex.Message}");
                    }
                }
            }
            foreach (var service in services)
                service.Dispose();
            device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        }

        void OnConnectionStatusChanged(BluetoothLEDevice changedDevice, object eventArgs)
        {
            logger.Info($"GATT connection status changed: {changedDevice.ConnectionStatus}.");
            if (changedDevice.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                disconnected.TrySetResult();
        }
    }

    private static ulong ParseAddress(string address)
    {
        var hex = new string(address.Where(char.IsAsciiHexDigit).ToArray());
        if (hex.Length != 12 || !ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var result))
            throw new ArgumentException("Bluetooth address must contain 12 hexadecimal digits.", nameof(address));
        return result;
    }

    private static bool RequiresReconnect(GattCommunicationStatus status) =>
        status != GattCommunicationStatus.Success;

    private static string FormatAddress(ulong address) => WindowsBleAdvertisementSource.FormatAddress(address);

    private static byte[] ReadBuffer(IBuffer? buffer)
    {
        if (buffer is null)
            return [];
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
