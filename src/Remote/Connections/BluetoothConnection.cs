using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Remote.Connections;

/// <summary>
/// Bluetooth Low Energy, for the C# / nanoFramework transmitter, which advertises as "NanoBrick"
/// with the Nordic UART service.
/// </summary>
public sealed class BluetoothConnection : IConnection
{
    // Nordic UART service: we write to RX, the transmitter answers with notifications on TX
    private static readonly Guid UartService = Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly Guid RxCharacteristic = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly Guid TxCharacteristic = Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");

    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(15);
    private const int ServiceDiscoveryAttempts = 5;

    private readonly string _deviceName;
    private readonly LineSplitter _lines = new();
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _rx, _tx;
    private GattWriteOption _writeOption = GattWriteOption.WriteWithResponse;

    public event Action<string>? LineReceived;
    public event Action? Lost;

    public BluetoothConnection(string deviceName = "NanoBrick")
    {
        _deviceName = deviceName;
    }

    public string Description => $"Bluetooth (\"{_deviceName}\")";

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ulong address = await FindDeviceAsync(cancellationToken);

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                  ?? throw new InvalidOperationException("Could not open the Bluetooth device.");
        _device.ConnectionStatusChanged += (device, _) =>
        {
            if (device.ConnectionStatus == BluetoothConnectionStatus.Disconnected) Lost?.Invoke();
        };

        _service = await DiscoverUartServiceAsync(_device, cancellationToken);

        _rx = (await _service.GetCharacteristicsForUuidAsync(RxCharacteristic)).Characteristics.FirstOrDefault()
              ?? throw new InvalidOperationException("RX characteristic not found.");
        _tx = (await _service.GetCharacteristicsForUuidAsync(TxCharacteristic)).Characteristics.FirstOrDefault()
              ?? throw new InvalidOperationException("TX characteristic not found.");

        // Write without response whenever possible: with a response, the nanoFramework ESP32 sometimes
        // reports error 0x8065000E although the command did arrive. The Bluetooth link layer still acks packets.
        if (_rx.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))
        {
            _writeOption = GattWriteOption.WriteWithoutResponse;
        }

        _tx.ValueChanged += OnNotification;
        var status = await _tx.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException("The transmitter refused notifications: " + status);
        }
    }

    public async Task SendAsync(string line)
    {
        if (_rx is null) return;
        var writer = new DataWriter();
        writer.WriteBytes(Encoding.ASCII.GetBytes(line + "\n"));
        var status = await _rx.WriteValueAsync(writer.DetachBuffer(), _writeOption);
        if (status != GattCommunicationStatus.Success) Lost?.Invoke();
    }

    // Listens to advertisements until the device with the expected name shows up
    private async Task<ulong> FindDeviceAsync(CancellationToken cancellationToken)
    {
        var found = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, advertisement) =>
        {
            if (advertisement.Advertisement.LocalName == _deviceName) found.TrySetResult(advertisement.BluetoothAddress);
        };

        watcher.Start();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ScanTimeout);
            using (timeout.Token.Register(() => found.TrySetCanceled()))
            {
                try
                {
                    return await found.Task;
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"No \"{_deviceName}\" device found within {ScanTimeout.TotalSeconds:0} s. " +
                        "Does the ESP32 run the C# firmware, and is the PC's Bluetooth on?");
                }
            }
        }
        finally
        {
            watcher.Stop();
        }
    }

    // Windows often fails service discovery right after a disconnection: retry, alternating cache modes
    private static async Task<GattDeviceService> DiscoverUartServiceAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < ServiceDiscoveryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 0) await Task.Delay(1000, cancellationToken);

            var cacheMode = attempt % 2 == 0 ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached;
            var result = await device.GetGattServicesForUuidAsync(UartService, cacheMode);
            if (result.Status == GattCommunicationStatus.Success && result.Services.Count > 0)
            {
                return result.Services[0];
            }
        }

        throw new InvalidOperationException(
            $"Nordic UART service not found on the transmitter ({ServiceDiscoveryAttempts} attempts).");
    }

    private void OnNotification(GattCharacteristic sender, GattValueChangedEventArgs e)
    {
        var reader = DataReader.FromBuffer(e.CharacteristicValue);
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);

        foreach (string line in _lines.Append(Encoding.ASCII.GetString(bytes))) LineReceived?.Invoke(line);
    }

    // Releases everything explicitly: otherwise Windows keeps the link open until garbage collection,
    // the ESP32 believes it is still connected and the next connection fails or drags on.
    public async ValueTask DisposeAsync()
    {
        if (_tx is not null)
        {
            _tx.ValueChanged -= OnNotification;
            try
            {
                await _tx.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch
            {
                // Already disconnected
            }
        }

        _service?.Session?.Dispose();
        _service?.Dispose();
        _device?.Dispose();
        _service = null;
        _device = null;
        _rx = _tx = null;
    }
}
