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
    private GattSession? _session;
    private GattCharacteristic? _writeChar;
    private GattCharacteristic? _notifyChar;
    private volatile bool _dropped;
    private CancellationTokenSource _bindCts = new();
    private DateTime _droppedAtUtc;
    private readonly System.Threading.Timer _watchdog;
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
        _watchdog = new System.Threading.Timer(_ => OnWatchdog(), null,
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
    public readonly record struct AdvertWait(bool Seen, short Rssi, int TotalAdverts, TimeSpan Elapsed);

    public static Task<AdvertWait> WaitForMacAdvertAsync(ulong mac, TimeSpan window, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<AdvertWait>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int total = 0;
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        watcher.Received += (_, args) =>
        {
            Interlocked.Increment(ref total);
            if (args.BluetoothAddress == mac)
            {
                try { watcher.Stop(); } catch { }
                tcs.TrySetResult(new AdvertWait(true, args.RawSignalStrengthInDBm, total, sw.Elapsed));
            }
        };

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(window);
        cts.Token.Register(() =>
        {
            try { watcher.Stop(); } catch { }
            tcs.TrySetResult(new AdvertWait(false, 0, total, sw.Elapsed));
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
        _bindCts.Cancel();
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await TearDownDeviceAsync().ConfigureAwait(false);
            _address = null;
            LastSentTempC = null;
            BatteryPercent = null;
            _bindCts.Dispose();
            _bindCts = new CancellationTokenSource();
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

    private async Task TryOpenAsync(CancellationToken outerCt)
    {
        if (_disposed || _address is null) return;
        var addr = _address.Value;
        // Unbind cancels this so it doesn't have to wait out a 25 s scan or a
        // slow GATT discovery while holding _connectLock.
        var bindToken = _bindCts.Token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt, bindToken);
        var ct = linked.Token;
        try
        {
            // Direct open first. If the OS still knows the device (it was
            // connected earlier, or Windows is still holding the link after a
            // drop), the sock isn't advertising and a scan would never see it.
            Logf($"Opening {FormatMac(addr)} …");
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr).AsTask(ct);
            if (_device is null)
            {
                // Not in the OS cache (typically right after a reboot):
                // WinRT only returns a device once it has seen an advert.
                var adv = await WaitForMacAdvertAsync(addr, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
                if (!adv.Seen)
                {
                    Logf($"Not in OS cache and no advert in 25 s ({adv.TotalAdverts} BLE adverts from other devices) — will retry.");
                    ScheduleReconnect();
                    return;
                }
                Logf($"Advert seen after {adv.Elapsed.TotalSeconds:0.0} s (RSSI {adv.Rssi} dBm).");
                _device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr).AsTask(ct)
                          ?? throw new InvalidOperationException("FromBluetoothAddressAsync returned null after advert");
            }

            _device.ConnectionStatusChanged += OnConnectionStatusChanged;

            // Ask Windows to keep the link up and reconnect on its own after a
            // drop. Its background reconnect works where our own advert scan
            // can't: while the OS is re-establishing the link, the sock stops
            // advertising.
            try
            {
                _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(ct).ConfigureAwait(false);
                if (_session is not null) _session.MaintainConnection = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Logf($"GattSession unavailable: {ex.Message}"); }

            await AttachGattAsync(ct).ConfigureAwait(false);

            Logf("Connected.");
            ConnectionChanged?.Invoke(this, true);

            // Kick a battery query right away so the UI gets a value fast,
            // then poll every 30 s. The vendor app polls at 3 s which is
            // overkill for a 10 Ah pack that drains over hours.
            _batteryPollTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException) when (bindToken.IsCancellationRequested)
        {
            await TearDownDeviceAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logf($"Connect failed: {ex.Message}");
            await TearDownDeviceAsync().ConfigureAwait(false);
            ScheduleReconnect();
        }
    }

    /// <summary>Fetch FFF0/FFF1/FFF2 fresh and subscribe to notifications.
    /// Used on first connect and again after an OS-side reconnect: once a
    /// link drops, Windows closes the old GattDeviceService, so the previous
    /// characteristic objects throw ObjectDisposedException.</summary>
    private async Task AttachGattAsync(CancellationToken ct)
    {
        if (_device is null) throw new InvalidOperationException("no device");
        if (_notifyChar is not null)
        {
            try { _notifyChar.ValueChanged -= OnNotify; } catch { }
            _notifyChar = null;
        }
        _writeChar = null;

        var svcResult = await _device
            .GetGattServicesForUuidAsync(SockProtocol.ServiceUuid, BluetoothCacheMode.Uncached)
            .AsTask(ct).ConfigureAwait(false);
        if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            throw new InvalidOperationException($"FFF0 service not found: {svcResult.Status}");
        var svc = svcResult.Services[0];

        var write  = await GetCharAsync(svc, SockProtocol.CharWrite,  ct).ConfigureAwait(false);
        var notify = await GetCharAsync(svc, SockProtocol.CharNotify, ct).ConfigureAwait(false);

        var cc = await notify
            .WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify)
            .AsTask(ct).ConfigureAwait(false);
        if (cc != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"FFF1 CCCD enable failed: {cc}");
        notify.ValueChanged += OnNotify;

        // Assigned last: a null _writeChar marks "still attaching", which
        // OnConnectionStatusChanged uses to ignore status flaps mid-setup.
        _notifyChar = notify;
        _writeChar = write;
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            if (_dropped || _writeChar is null) return;
            _dropped = true;
            _droppedAtUtc = DateTime.UtcNow;
            Logf("Disconnected — waiting for Windows to restore the link.");
            _batteryPollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            // The sock may have power-cycled; force the next reconcile to
            // resend the target instead of trusting the pre-drop value.
            LastSentTempC = null;
            ConnectionChanged?.Invoke(this, false);
            // Keep the device + session: MaintainConnection makes Windows
            // reconnect in the background. Only if that hasn't happened in
            // OsReconnectGrace do we tear down and start over.
            _watchdog.Change(OsReconnectGrace, Timeout.InfiniteTimeSpan);
        }
        else if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected && _dropped)
        {
            _ = ResumeAfterOsReconnectAsync();
        }
    }

    private static readonly TimeSpan OsReconnectGrace = TimeSpan.FromSeconds(90);

    private async Task ResumeAfterOsReconnectAsync()
    {
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_dropped || _device is null) return;
            var down = DateTime.UtcNow - _droppedAtUtc;
            // Fresh GATT objects (the old ones are closed after a drop) and a
            // fresh notify subscription (the firmware doesn't bond).
            await AttachGattAsync(_bindCts.Token).ConfigureAwait(false);
            _dropped = false;
            _watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            Logf($"Reconnected by Windows after {down.TotalSeconds:0} s.");
            _batteryPollTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
            ConnectionChanged?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            Logf($"Resume after reconnect failed: {ex.Message} — full reconnect.");
            _dropped = false;
            await TearDownDeviceAsync().ConfigureAwait(false);
            ScheduleReconnect();
        }
        finally { _connectLock.Release(); }
    }

    private void OnWatchdog()
    {
        if (!_dropped || _disposed || !_reconnectRequested) return;
        Logf($"Windows didn't restore the link within {OsReconnectGrace.TotalSeconds:0} s — reopening.");
        _ = Task.Run(async () =>
        {
            await _connectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_dropped) return;
                _dropped = false;
                await TearDownDeviceAsync().ConfigureAwait(false);
                await TryOpenAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally { _connectLock.Release(); }
        });
    }

    private void ScheduleReconnect()
    {
        if (!_reconnectRequested || _disposed) return;
        // Tied to the current binding: an unbind (e.g. a swap) cancels it, so
        // a retry planned for the old binding can't become a second retry
        // chain next to the new one.
        var bindToken = _bindCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), bindToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (_disposed || !_reconnectRequested || IsConnected) return;
            await _connectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || !_reconnectRequested || IsConnected || bindToken.IsCancellationRequested) return;
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
        _watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _dropped = false;
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
        if (_session is not null)
        {
            try { _session.MaintainConnection = false; } catch { }
            try { _session.Dispose(); } catch { }
            _session = null;
        }
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
        try { _bindCts.Cancel(); } catch { }
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try { await TearDownDeviceAsync().ConfigureAwait(false); } catch { }
        finally { _connectLock.Release(); }
        try { _bindCts.Dispose(); } catch { }
        try { _batteryPollTimer.Dispose(); } catch { }
        try { _watchdog.Dispose(); } catch { }
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
