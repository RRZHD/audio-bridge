using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace AudioBridge.Server.Core;

/// <summary>
/// Bluetooth LE counterpart of the phone's GATT server (see BleTransport.kt): scans for the phone
/// advertising the Audio Bridge service, connects, switches the TX notifications on and exposes the
/// two characteristics as one duplex byte stream — the same wire protocol as every other transport.
///
/// Why LE: the classic RFCOMM link stalled for 100-900 ms at a time on the test phone; an LE
/// connection is scheduled in fixed connection events that the controller serves before scanning.
/// </summary>
public sealed class BleServerListener : IDisposable
{
    // Must match android-app/.../transport/BleTransport.kt
    public static readonly Guid ServiceUuid = new("7c9f1a2e-4b3d-4c7a-9e1f-2a6b8d4c5e40");
    public static readonly Guid RxUuid = new("7c9f1a2e-4b3d-4c7a-9e1f-2a6b8d4c5e41"); // PC -> phone (write without response)
    public static readonly Guid TxUuid = new("7c9f1a2e-4b3d-4c7a-9e1f-2a6b8d4c5e42"); // phone -> PC (notify)

    private BluetoothLEAdvertisementWatcher? _watcher;
    private int _busy; // 1 while connecting or connected: ignore further advertisements
    private CancellationTokenSource? _cts;

    public event Action<Stream, string>? ClientAccepted;
    public event Action<string>? Log;

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(ServiceUuid);
        watcher.Received += (sender, args) =>
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
            var ct = _cts?.Token ?? CancellationToken.None;
            _ = Task.Run(() => ConnectAsync(args.BluetoothAddress, ct), ct);
        };
        watcher.Stopped += (sender, args) =>
        {
            if (args.Error != BluetoothError.Success) Log?.Invoke($"Bluetooth LE: сканирование остановлено ({args.Error})");
        };
        watcher.Start();
        _watcher = watcher;
        Log?.Invoke("Bluetooth LE: ищу телефон (в приложении выбери «Bluetooth LE» и нажми кнопку включения)");
    }

    private async Task ConnectAsync(ulong address, CancellationToken ct)
    {
        BluetoothLEDevice? device = null;
        GattSession? session = null;
        try
        {
            Log?.Invoke($"Bluetooth LE: нашёл телефон ({address:X12}), подключаюсь…");
            device = await Step("FromBluetoothAddressAsync", BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(), ct);
            if (device == null) throw new IOException("устройство недоступно");
            Log?.Invoke($"Bluetooth LE: устройство открыто, состояние {device.ConnectionStatus}");

            // Keep the connection and let the MTU grow: the audio frames are a few hundred bytes.
            session = await Step("GattSession", GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(), ct);
            session.MaintainConnection = true;

            // The first GATT call after FromBluetoothAddressAsync is what actually brings the link up and
            // often answers "Unreachable" while the connection is still being established: retry.
            GattDeviceServicesResult? services = null;
            for (var attempt = 1; attempt <= 6; attempt++)
            {
                services = await Step("поиск сервиса", device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached).AsTask(), ct);
                Log?.Invoke($"Bluetooth LE: поиск сервиса, попытка {attempt}: {services.Status}, найдено {services.Services.Count}");
                if (services.Status == GattCommunicationStatus.Success && services.Services.Count > 0) break;
                ct.ThrowIfCancellationRequested();
                await Task.Delay(700, ct);
            }
            if (services == null || services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new IOException($"сервис не найден ({services?.Status})");
            var service = services.Services[0];

            // Look the characteristics up ourselves (and say what the phone really exposes): a
            // failed lookup by UUID gives no hint why.
            GattCharacteristicsResult all = await Step("поиск характеристик", service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(), ct);
            for (var attempt = 1; attempt < 4 && (all.Status != GattCommunicationStatus.Success || all.Characteristics.Count == 0); attempt++)
            {
                await Task.Delay(700, ct);
                all = await Step("поиск характеристик", service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(), ct);
            }
            var found = all.Status == GattCommunicationStatus.Success
                ? string.Join(", ", System.Linq.Enumerable.Select(all.Characteristics, c => c.Uuid.ToString().Substring(30)))
                : $"статус {all.Status}, код протокола {all.ProtocolError}";
            GattCharacteristic? rxChar = null, txChar = null;
            if (all.Status == GattCommunicationStatus.Success)
            {
                foreach (var c in all.Characteristics)
                {
                    if (c.Uuid == RxUuid) rxChar = c;
                    else if (c.Uuid == TxUuid) txChar = c;
                }
            }
            if (rxChar == null || txChar == null) throw new IOException($"характеристики не найдены (есть: {found})");

            var stream = new BleStream(rxChar, txChar, session);

            // Ask Windows for short connection intervals (Windows 11): audio needs several packets per 20 ms.
            try { device.RequestPreferredConnectionParameters(BluetoothLEPreferredConnectionParameters.ThroughputOptimized); }
            catch (Exception ex) { Log?.Invoke($"Bluetooth LE: параметры соединения не изменены ({ex.Message})"); }

            var status = await Step("включение уведомлений", txChar.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(), ct);
            if (status != GattCommunicationStatus.Success) throw new IOException($"уведомления не включены ({status})");

            var name = string.IsNullOrWhiteSpace(device.Name) ? "телефон" : device.Name;
            var d = device;
            var s = session;
            device.ConnectionStatusChanged += (_, _) =>
            {
                if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected) stream.Dispose();
            };
            stream.Closed += () =>
            {
                try { s.Dispose(); } catch { /* already gone */ }
                try { service.Dispose(); } catch { /* ignore */ }
                try { d.Dispose(); } catch { /* ignore */ }
                Interlocked.Exchange(ref _busy, 0); // scanning is still running: the phone can reconnect
                Log?.Invoke("Bluetooth LE: соединение закрыто");
            };

            Log?.Invoke($"Bluetooth LE: подключён к «{name}», MTU {session.MaxPduSize}");
            ClientAccepted?.Invoke(stream, $"Bluetooth LE ({name})");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Bluetooth LE: не удалось подключиться — {ex.Message}");
            try { session?.Dispose(); } catch { /* ignore */ }
            try { device?.Dispose(); } catch { /* ignore */ }
            // A short pause so a failing phone is not retried in a hot loop.
            try { await Task.Delay(2000, ct); } catch { /* stopping */ }
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>One WinRT step with a timeout, so a hung GATT call becomes a readable error instead of silence.</summary>
    private static async Task<T> Step<T>(string what, Task<T> operation, CancellationToken ct, int timeoutMs = 12000)
    {
        var done = await Task.WhenAny(operation, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
        if (done != operation) throw new TimeoutException($"{what}: нет ответа {timeoutMs / 1000} с");
        return await operation.ConfigureAwait(false);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _watcher?.Stop(); } catch { /* ignore */ }
        _watcher = null;
        Interlocked.Exchange(ref _busy, 0);
    }

    public void Dispose() => Stop();
}

/// <summary>The two GATT characteristics as a duplex byte stream.</summary>
internal sealed class BleStream : Stream
{
    private readonly GattCharacteristic _rx;
    private readonly GattCharacteristic _tx;
    private readonly GattSession _session;
    private readonly BlockingCollection<byte[]> _incoming = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private byte[]? _current;
    private int _pos;
    private int _closed;

    public event Action? Closed;

    public BleStream(GattCharacteristic rx, GattCharacteristic tx, GattSession session)
    {
        _rx = rx;
        _tx = tx;
        _session = session;
        _tx.ValueChanged += OnValueChanged;
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        if (!_incoming.IsAddingCompleted)
        {
            try { _incoming.Add(bytes); } catch (InvalidOperationException) { /* closing */ }
        }
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0) return 0;
        while (_current == null || _pos >= _current.Length)
        {
            try
            {
                if (!_incoming.TryTake(out _current, 500))
                {
                    _current = null;
                    if (Volatile.Read(ref _closed) != 0) return 0;
                    continue;
                }
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
            _pos = 0;
        }
        var n = Math.Min(count, _current.Length - _pos);
        System.Buffer.BlockCopy(_current, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // ATT payload = negotiated PDU - 3 (capped: the phone's server notifies at most 244).
            var chunk = Math.Clamp((int)_session.MaxPduSize - 3, 20, 244);
            var sent = 0;
            while (sent < buffer.Length)
            {
                if (Volatile.Read(ref _closed) != 0) throw new IOException("Bluetooth LE: соединение закрыто");
                var n = Math.Min(chunk, buffer.Length - sent);
                var writer = new DataWriter();
                writer.WriteBytes(buffer.Slice(sent, n).ToArray());
                var status = await _rx.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
                if (status != GattCommunicationStatus.Success)
                    throw new IOException($"Bluetooth LE: запись не удалась ({status})");
                sent += n;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(() => Read(buffer, offset, count), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var tmp = new byte[buffer.Length];
        return new ValueTask<int>(Task.Factory.StartNew(() =>
        {
            var n = Read(tmp, 0, tmp.Length);
            tmp.AsSpan(0, n).CopyTo(buffer.Span);
            return n;
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default));
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _tx.ValueChanged -= OnValueChanged; } catch { /* ignore */ }
        _incoming.CompleteAdding();
        Closed?.Invoke();
        base.Dispose(disposing);
    }
}
