using System.Collections.Concurrent;
using System.Linq;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace ReonOSC.Ble;

/// <summary>
/// Managed BLE wrapper for one of the DRCODC heated socks. The firmware is
/// authless, so a client just needs the device's MAC — see
/// <see cref="SockProtocol"/> for the wire protocol.
///
/// On Windows the advertising for these socks is spotty (reportedly a Barrot
/// dongle issue), so the policy is: once we have a MAC, keep the connection
/// open and reconnect in the background on drop rather than scan each time.
/// Callers construct a client, call <see cref="BindAsync"/> with the MAC,
/// and from then on <see cref="SetTemperatureAsync"/> just writes whenever
/// the socket is up. If it's down, the write is dropped silently (the
/// higher-layer resolver will re-fire on the next OSC/manual event anyway).
/// </summary>
public sealed class SockClient : IAsyncDisposable
{
    /// <summary>Label purely for log lines and the per-sock event origin.
    /// L/R assignment lives higher up — SockClient itself only knows its MAC.</summary>
    public enum Side { Left, Right }

    public Side SockSide { get; }

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _writeChar;
    private GattCharacteristic? _notifyChar;
    private ulong? _address;
    private volatile bool _disposed;
    private volatile bool _reconnectRequested;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly System.Threading.Timer _batteryPollTimer;

    /// <summary>Last battery percent reported via FFF1 notify, or null if the
    /// device hasn't answered a battery query yet (or isn't connected).</summary>
    public int? BatteryPercent { get; private set; }

    /// <summary>Last temperature we successfully wrote to the sock, or null
    /// if nothing has been sent this session. Used by the higher layer to
    /// skip redundant writes.</summary>
    public byte? LastSentTempC { get; private set; }

    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected
                               && _writeChar is not null;

    public ulong? Address => _address;

    public event EventHandler<int>? BatteryChanged;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<string>? Log;

    public SockClient(Side side)
    {
        SockSide = side;
        _batteryPollTimer = new System.Threading.Timer(_ => _ = TryQueryBatteryAsync(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private string Tag => SockSide == Side.Left ? "SockL" : "SockR";

    public static ulong ParseMac(string mac) => ReonClient.ParseMac(mac);
    public static string FormatMac(ulong mac) => ReonClient.FormatMac(mac);

    /// <summary>
    /// Scan for sock advertisements for the given window and return every
    /// unique MAC that advertised the DRCODC local name during it. Used
    /// once at pair time so the user can bind each of the two MACs to a
    /// foot in the UI.
    /// </summary>
    public static Task<IReadOnlyList<ulong>> ScanForSocksAsync(TimeSpan window, CancellationToken ct = default)
    {
        var seen = new ConcurrentDictionary<ulong, byte>();
        var tcs = new TaskCompletionSource<IReadOnlyList<ulong>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        void Finish()
        {
            try { watcher.Stop(); } catch { }
            tcs.TrySetResult(seen.Keys.ToArray());
        }

        watcher.Received += (_, args) =>
        {
            var name = args.Advertisement?.LocalName;
            if (string.Equals(name, SockProtocol.DeviceLocalName, StringComparison.Ordinal))
                seen.TryAdd(args.BluetoothAddress, 0);
        };

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(window);
        cts.Token.Register(Finish);

        watcher.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Wait for the given MAC to show up in a BLE advertisement, up to
    /// <paramref name="window"/>. Returns true as soon as it's seen, false
    /// on timeout. WinRT's FromBluetoothAddressAsync returns null when the
    /// OS hasn't cached the device recently — confirmed on at least two
    /// different Windows hosts with different dongles, so this is a
    /// WinRT-level behaviour, not a dongle quirk. A prior successful scan
    /// hit is what makes the subsequent connect work.
    /// </summary>
    public static Task<bool> WaitForMacAdvertAsync(ulong mac, TimeSpan window, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        watcher.Received += (_, args) =>
        {
            if (args.BluetoothAddress == mac)
            {
                try { watcher.Stop(); } catch { }
                tcs.TrySetResult(true);
            }
        };

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(window);
        cts.Token.Register(() =>
        {
            try { watcher.Stop(); } catch { }
            tcs.TrySetResult(false);
        });

        watcher.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Bind this client to a MAC and open a connection. On disconnect it
    /// will reconnect in the background until <see cref="DisposeAsync"/>.
    /// Idempotent — rebinding to the same address is a no-op if already
    /// connected; a different address replaces the current one.
    /// </summary>
    public async Task BindAsync(ulong address, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SockClient));
        if (_address == address && IsConnected) return;

        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_address != address)
            {
                await TearDownDeviceAsync().ConfigureAwait(false);
                _address = address;
                LastSentTempC = null;
                BatteryPercent = null;
            }
            _reconnectRequested = true;
            await TryOpenAsync(ct).ConfigureAwait(false);
        }
        finally { _connectLock.Release(); }
    }

    /// <summary>Stop auto-reconnect and release the current BLE handle.
    /// After unbind the client is dormant until <see cref="BindAsync"/>
    /// is called again (or <see cref="DisposeAsync"/>).</summary>
    public async Task UnbindAsync()
    {
        _reconnectRequested = false;
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await TearDownDeviceAsync().ConfigureAwait(false);
            _address = null;
            LastSentTempC = null;
            BatteryPercent = null;
        }
        finally { _connectLock.Release(); }
        ConnectionChanged?.Invoke(this, false);
    }

    /// <summary>
    /// Write the SetTemperature frame to the sock. 0 = off, otherwise clamped
    /// to [35, 70]. Returns true if the write made it to the firmware.
    /// Dropped silently (returns false) if the socket isn't connected — the
    /// resolver will retry on the next state change.
    /// </summary>
    public async Task<bool> SetTemperatureAsync(byte tempC, CancellationToken ct = default)
    {
        if (!IsConnected) return false;
        byte normalised = tempC == 0 ? (byte)0
                        : (byte)Math.Clamp(tempC, SockProtocol.TempMin, SockProtocol.TempMax);
        if (LastSentTempC == normalised) return true;

        if (!await _writeLock.WaitAsync(0, ct).ConfigureAwait(false)) return false;
        try
        {
            if (_writeChar is null) return false;
            var frame = SockProtocol.BuildSetTemperature(normalised);
            try
            {
                await WriteAsync(_writeChar, frame, ct).ConfigureAwait(false);
                LastSentTempC = normalised;
                Logf(normalised == 0 ? "Set OFF." : $"Set {normalised} °C.");
                return true;
            }
            catch (Exception ex)
            {
                Logf($"Write failed: {ex.Message}");
                return false;
            }
        }
        finally { _writeLock.Release(); }
    }

    public Task<bool> TurnOffAsync(CancellationToken ct = default) =>
        SetTemperatureAsync(0, ct);

    private async Task TryQueryBatteryAsync()
    {
        if (!IsConnected || _writeChar is null) return;
        try
        {
            await WriteAsync(_writeChar, SockProtocol.BuildBatteryQuery(), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch { /* polling, ignore */ }
    }

    // ---- connection lifecycle --------------------------------------------

    private async Task TryOpenAsync(CancellationToken ct)
    {
        if (_disposed || _address is null) return;
        try
        {
            // WinRT's FromBluetoothAddressAsync returns null until the OS
            // has cached the device via a recent advertisement — confirmed
            // on at least two different Windows hosts with different
            // dongles, so this is a stack-level behaviour, not a dongle
            // quirk. The vendor app only ever works right after a scan;
            // after a reboot the socks aren't in the cache yet. Prime by
            // scanning for the specific MAC first.
            Logf($"Waiting for advertisement from {FormatMac(_address.Value)} …");
            var seen = await WaitForMacAdvertAsync(_address.Value, TimeSpan.FromSeconds(25), ct)
                .ConfigureAwait(false);
            if (!seen)
            {
                Logf("No advertisement in 25 s — will retry.");
                ScheduleReconnect();
                return;
            }

            Logf($"Connecting to {FormatMac(_address.Value)} …");
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(_address.Value).AsTask(ct)
                      ?? throw new InvalidOperationException("FromBluetoothAddressAsync returned null");

            _device.ConnectionStatusChanged += OnConnectionStatusChanged;

            var svcResult = await _device
                .GetGattServicesForUuidAsync(SockProtocol.ServiceUuid, BluetoothCacheMode.Uncached)
                .AsTask(ct).ConfigureAwait(false);
            if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
                throw new InvalidOperationException($"FFF0 service not found: {svcResult.Status}");
            var svc = svcResult.Services[0];

            _writeChar  = await GetCharAsync(svc, SockProtocol.CharWrite,  ct).ConfigureAwait(false);
            _notifyChar = await GetCharAsync(svc, SockProtocol.CharNotify, ct).ConfigureAwait(false);

            var ccResult = await _notifyChar
                .WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify)
                .AsTask(ct).ConfigureAwait(false);
            if (ccResult != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"FFF1 CCCD enable failed: {ccResult}");
            _notifyChar.ValueChanged += OnNotify;

            Logf("Connected.");
            ConnectionChanged?.Invoke(this, true);

            // Kick a battery query right away so the UI gets a value fast,
            // then poll every 30 s. The vendor app polls at 3 s which is
            // overkill for a 10 Ah pack that drains over hours.
            _batteryPollTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            Logf($"Connect failed: {ex.Message}");
            await TearDownDeviceAsync().ConfigureAwait(false);
            ScheduleReconnect();
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            Logf("Disconnected.");
            _batteryPollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            ConnectionChanged?.Invoke(this, false);
            // Keep the device object around until the reconnect pass tears
            // it down — Windows sometimes recovers the connection on its own
            // if we don't yank the handle.
            ScheduleReconnect();
        }
    }

    private void ScheduleReconnect()
    {
        if (!_reconnectRequested || _disposed) return;
        _ = Task.Run(async () =>
        {
            // Simple fixed backoff. Windows will internally keep trying to
            // re-attach to the known MAC — we just drive the retry cadence
            // and surface progress to the log.
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (_disposed || !_reconnectRequested || IsConnected) return;
            await _connectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || !_reconnectRequested || IsConnected) return;
                await TearDownDeviceAsync().ConfigureAwait(false);
                await TryOpenAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally { _connectLock.Release(); }
        });
    }

    private void OnNotify(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = BufferToBytes(args.CharacteristicValue);
        var pct = SockProtocol.TryDecodeBatteryNotify(data);
        if (pct is int p)
        {
            if (BatteryPercent != p)
            {
                BatteryPercent = p;
                BatteryChanged?.Invoke(this, p);
            }
        }
    }

    private async Task TearDownDeviceAsync()
    {
        _batteryPollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_notifyChar is not null)
        {
            try
            {
                _notifyChar.ValueChanged -= OnNotify;
                await _notifyChar
                    .WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None)
                    .AsTask().ConfigureAwait(false);
            }
            catch { }
            _notifyChar = null;
        }
        _writeChar = null;
        if (_device is not null)
        {
            try { _device.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }
            try { _device.Dispose(); } catch { }
            _device = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _reconnectRequested = false;
        try { _batteryPollTimer.Dispose(); } catch { }
        try { await TearDownDeviceAsync().ConfigureAwait(false); } catch { }
        _writeLock.Dispose();
        _connectLock.Dispose();
    }

    // ---- GATT helpers -----------------------------------------------------

    private static async Task<GattCharacteristic> GetCharAsync(
        GattDeviceService svc, Guid uuid, CancellationToken ct)
    {
        var r = await svc.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached)
            .AsTask(ct).ConfigureAwait(false);
        if (r.Status != GattCommunicationStatus.Success || r.Characteristics.Count == 0)
            throw new InvalidOperationException($"Characteristic {uuid} not found: {r.Status}");
        return r.Characteristics[0];
    }

    private static async Task WriteAsync(GattCharacteristic ch, byte[] data, CancellationToken ct)
    {
        var writer = new DataWriter();
        writer.WriteBytes(data);
        var result = await ch
            .WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse)
            .AsTask(ct).ConfigureAwait(false);
        if (result.Status == GattCommunicationStatus.Success) return;
        throw new InvalidOperationException($"GATT write failed: {result.Status}");
    }

    private static byte[] BufferToBytes(IBuffer buffer)
    {
        var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private void Logf(string msg) => Log?.Invoke(this, $"[{Tag}] {msg}");
}
