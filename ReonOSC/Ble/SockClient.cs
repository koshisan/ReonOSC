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
public sealed class SockClient : ISockLink
{
    /// <summary>Label purely for log lines and the per-sock event origin.
    /// L/R assignment lives higher up — SockClient itself only knows its MAC.</summary>
    public enum Side { Left, Right }

    public Side SockSide { get; }

    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattDeviceService? _gattService;
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

    /// <summary>Last battery voltage reported via FFF1 notify, or null if the
    /// device hasn't answered a battery query yet (or isn't connected).</summary>
    public double? BatteryVolts => _batteryDeciVolts is int dv ? dv / 10.0 : null;

    /// <summary>Rough charge estimate derived from <see cref="BatteryVolts"/>.</summary>
    public int? BatteryPercent => _batteryDeciVolts is int dv ? SockProtocol.EstimatePercent(dv) : null;

    private int? _batteryDeciVolts;

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
        _keepAliveTimer = new System.Threading.Timer(_ => _ = KeepAliveAsync(), null,
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
                _batteryDeciVolts = null;
            }
            _reconnectRequested = true;
            SetHealth(LinkHealth.Connecting);
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
            _batteryDeciVolts = null;
            _failStreak = 0;
            _bindCts.Dispose();
            _bindCts = new CancellationTokenSource();
        }
        finally { _connectLock.Release(); }
        StopPresenceWatch();
        SetHealth(LinkHealth.Unbound);
        ConnectionChanged?.Invoke(this, false);
    }

    /// <summary>
    /// Write the SetTemperature frame to the sock. 0 = off, otherwise clamped
    /// to [35, 70]. Returns true if the write made it to the firmware.
    /// Dropped silently (returns false) if the socket isn't connected — the
    /// resolver will retry on the next state change.
    /// </summary>
    public Task<bool> SetTemperatureAsync(byte tempC, CancellationToken ct = default) =>
        SetTemperatureCoreAsync(tempC, TimeSpan.Zero, ct);

    /// <summary>Unlike a normal set, waits for an in-flight write instead of
    /// skipping: this is the shutdown path, there is no next reconcile.</summary>
    public Task<bool> TurnOffAsync(CancellationToken ct = default) =>
        SetTemperatureCoreAsync(0, TimeSpan.FromSeconds(2), ct);

    private async Task<bool> SetTemperatureCoreAsync(byte tempC, TimeSpan lockWait, CancellationToken ct)
    {
        if (!IsConnected) return false;
        byte normalised = tempC == 0 ? (byte)0
                        : (byte)Math.Clamp(tempC, SockProtocol.TempMin, SockProtocol.TempMax);
        if (LastSentTempC == normalised) return true;

        if (!await WriteFrameAsync(SockProtocol.BuildSetTemperature(normalised), lockWait, ct).ConfigureAwait(false))
            return false;
        LastSentTempC = normalised;
        Logf(normalised == 0 ? "Set OFF." : $"Set {normalised} °C.");
        return true;
    }

    /// <summary>Serialized write to FFF2. Returns false if the lock wasn't
    /// free within <paramref name="lockWait"/> or the write failed.</summary>
    private async Task<bool> WriteFrameAsync(byte[] frame, TimeSpan lockWait, CancellationToken ct)
    {
        if (!await _writeLock.WaitAsync(lockWait, ct).ConfigureAwait(false)) return false;
        try
        {
            if (_writeChar is null) return false;
            await WriteAsync(_writeChar, frame, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Logf($"Write failed: {ex.Message}");
            return false;
        }
        finally { _writeLock.Release(); }
    }

    // ---- dead-man keep-alive ----------------------------------------------

    /// <summary>Minutes after which the sock's own timer switches heating off
    /// unless we refresh it; 0 disables. If the link drops or the app dies, the
    /// sock then stops heating by itself instead of holding its last target.</summary>
    public int DeadManMinutes
    {
        get => _deadManMinutes;
        set
        {
            var v = Math.Clamp(value, 0, 255);
            if (v == _deadManMinutes) return;
            _deadManMinutes = v;
            if (IsConnected) StartKeepAlive();
        }
    }
    private int _deadManMinutes;
    private readonly System.Threading.Timer _keepAliveTimer;

    // Refresh three times per dead-man window so one missed write can't let
    // it expire while we're connected; at least every 15 s, at most every 5 min.
    private TimeSpan KeepAliveInterval => _deadManMinutes == 0
        ? TimeSpan.FromMinutes(5)
        : TimeSpan.FromSeconds(Math.Clamp(_deadManMinutes * 60 / 3, 15, 300));

    private void StartKeepAlive() =>
        _keepAliveTimer.Change(TimeSpan.Zero, KeepAliveInterval);

    private void StopKeepAlive() =>
        _keepAliveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private async Task KeepAliveAsync()
    {
        if (!IsConnected) return;
        // Re-arm the timer (it restarts on every write, if the firmware
        // behaves like the vendor app expects), then resend the target: if
        // the timer or the cuff button changed the sock's state behind our
        // back, the resolver would otherwise never notice, since it only
        // writes on change.
        await WriteFrameAsync(SockProtocol.BuildTimer((byte)_deadManMinutes), TimeSpan.FromSeconds(2), CancellationToken.None)
            .ConfigureAwait(false);
        if (LastSentTempC is byte t)
            await WriteFrameAsync(SockProtocol.BuildSetTemperature(t), TimeSpan.FromSeconds(2), CancellationToken.None)
                .ConfigureAwait(false);
    }

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
        var attemptStart = DateTime.UtcNow;
        if (_failStreak == 0) _streakStartUtc = attemptStart;
        try
        {
            // Direct open first. If the OS still knows the device (it was
            // connected earlier, or Windows is still holding the link after a
            // drop), the sock isn't advertising and a scan would never see it.
            if (_failStreak == 0) Logf($"Opening {FormatMac(addr)} …");
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr).AsTask(ct);
            if (_device is null)
            {
                // Not in the OS cache (typically right after a reboot):
                // WinRT only returns a device once it has seen an advert.
                var adv = await WaitForMacAdvertAsync(addr, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
                if (!adv.Seen)
                {
                    NoteFailure($"not in OS cache and no advert in 25 s ({adv.TotalAdverts} adverts from other devices)");
                    ScheduleReconnect(attemptStart);
                    return;
                }
                Logf($"Advert seen after {adv.Elapsed.TotalSeconds:0.0} s (RSSI {adv.Rssi} dBm).");
                _lastAdvertUtc = DateTime.UtcNow;
                _device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr).AsTask(ct)
                          ?? throw new InvalidOperationException("FromBluetoothAddressAsync returned null after advert");
            }

            _device.ConnectionStatusChanged += OnConnectionStatusChanged;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                _device.ConnectionParametersChanged += OnConnectionParametersChanged;

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

            Logf(_failStreak == 0
                ? "Connected."
                : $"Connected after {_failStreak} failed attempts ({(DateTime.UtcNow - _streakStartUtc).TotalSeconds:0} s).");
            _failStreak = 0;
            _connectedAtUtc = DateTime.UtcNow;
            StopPresenceWatch();
            LogLinkParameters("link");
            RequestLatencyZero();
            SetHealth(LinkHealth.Connected);
            ConnectionChanged?.Invoke(this, true);
            StartKeepAlive();

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
            NoteFailure(ex.Message);
            await TearDownDeviceAsync().ConfigureAwait(false);
            ScheduleReconnect(attemptStart);
        }
    }

    private int _failStreak;
    private DateTime _streakStartUtc;

    /// <summary>Retries now run back to back, so log the first failure of a
    /// streak and then a summary every 10 attempts instead of every line.</summary>
    private void NoteFailure(string reason)
    {
        _failStreak++;
        if (_failStreak == 1)
        {
            Logf($"Connect failed: {reason} — retrying continuously.");
            StartPresenceWatch();
        }
        else if (_failStreak % 10 == 0)
            Logf($"Still not connected after {_failStreak} attempts ({(DateTime.UtcNow - _streakStartUtc).TotalSeconds:0} s); last: {reason}");
        EvaluateHealth();
    }

    // ---- stuck / absent detection -----------------------------------------

    /// <summary>What the UI and HA show while not connected. Stuck = the sock
    /// advertises but has refused every connection for a while; field tests
    /// showed only unplugging its battery for a few seconds clears that.
    /// Absent = no adverts at all: off, flat, or out of range.</summary>
    public enum LinkHealth { Unbound, Connecting, Connected, Stuck, Absent }

    public LinkHealth Health { get; private set; } = LinkHealth.Unbound;
    public event EventHandler<LinkHealth>? HealthChanged;

    // Ordinary reconnects of the weaker sock took up to ~2 min in the field,
    // so 2 min flagged healthy-but-slow reconnects. Its adverts come only
    // every 10–40 s, hence the long "recent" window.
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AdvertRecent = TimeSpan.FromMinutes(3);
    private BluetoothLEAdvertisementWatcher? _presenceWatcher;
    private DateTime _lastAdvertUtc;

    private void EvaluateHealth()
    {
        var now = DateTime.UtcNow;
        if (now - _streakStartUtc < StuckAfter) SetHealth(LinkHealth.Connecting);
        else if (now - _lastAdvertUtc < AdvertRecent) SetHealth(LinkHealth.Stuck);
        else SetHealth(LinkHealth.Absent);
    }

    private void SetHealth(LinkHealth h)
    {
        if (h == Health) return;
        Health = h;
        if (h == LinkHealth.Stuck)
            Logf("Advertising but no connection for 5+ min.");
        else if (h == LinkHealth.Absent)
            Logf("No adverts for 3+ min — sock off, battery flat or out of range.");
        HealthChanged?.Invoke(this, h);
    }

    // Active: a passive scan missed enough of the sock's rare adverts to
    // report it absent while it was in fact advertising.
    private void StartPresenceWatch()
    {
        if (_presenceWatcher is not null || _address is null) return;
        var mac = _address.Value;
        var w = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        w.Received += (_, a) => { if (a.BluetoothAddress == mac) _lastAdvertUtc = DateTime.UtcNow; };
        try { w.Start(); _presenceWatcher = w; }
        catch (Exception ex) { Logf($"Presence watcher unavailable: {ex.Message}"); }
    }

    private void StopPresenceWatch()
    {
        var w = _presenceWatcher;
        _presenceWatcher = null;
        if (w is not null) try { w.Stop(); } catch { }
    }

    /// <summary>Log the negotiated connection interval, slave latency,
    /// supervision timeout and PHY. Evidence for why links drop: a short
    /// supervision timeout combined with a radio that misses events would
    /// explain the drops seen with one of the socks.</summary>
    private void LogLinkParameters(string what)
    {
        if (_device is null || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        try
        {
            var p = _device.GetConnectionParameters();
            var phy = _device.GetConnectionPhy();
            string Phy(BluetoothLEConnectionPhyInfo i) =>
                i.IsUncoded2MPhy ? "2M" : i.IsUncoded1MPhy ? "1M" : i.IsCodedPhy ? "Coded" : "?";
            Logf($"{what}: interval {p.ConnectionInterval * 1.25:0.##} ms, latency {p.ConnectionLatency}, " +
                 $"supervision timeout {p.LinkTimeout * 10} ms, PHY tx {Phy(phy.TransmitInfo)} / rx {Phy(phy.ReceiveInfo)}");
        }
        catch (Exception ex) { Logf($"{what}: parameters unavailable ({ex.Message})"); }
    }

    /// <summary>Fetch FFF0/FFF1/FFF2 fresh and subscribe to notifications.
    /// Used on first connect and again after an OS-side reconnect: once a
    /// link drops, Windows closes the old GattDeviceService, so the previous
    /// characteristic objects throw ObjectDisposedException.</summary>
    private async Task AttachGattAsync(CancellationToken ct)
    {
        if (_device is null) throw new InvalidOperationException("no device");
        ReleaseGattObjects();

        var svcResult = await _device
            .GetGattServicesForUuidAsync(SockProtocol.ServiceUuid, BluetoothCacheMode.Uncached)
            .AsTask(ct).ConfigureAwait(false);
        if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            throw new InvalidOperationException($"FFF0 service not found: {svcResult.Status}");
        var svc = svcResult.Services[0];
        for (int i = 1; i < svcResult.Services.Count; i++)
            try { svcResult.Services[i].Dispose(); } catch { }
        // Held until teardown: an undisposed GattDeviceService keeps the
        // service open in this process, and the next client to open the same
        // sock (e.g. the other foot after a swap) gets AccessDenied.
        _gattService = svc;

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
            var connectedFor = DateTime.UtcNow - _connectedAtUtc;
            Logf($"Disconnected after {connectedFor.TotalMinutes:0.0} min connected — reconnecting.");
            _batteryPollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            StopKeepAlive();
            SetHealth(LinkHealth.Connecting);
            // The sock may have power-cycled; force the next reconcile to
            // resend the target instead of trusting the pre-drop value.
            LastSentTempC = null;
            ConnectionChanged?.Invoke(this, false);
            // Field logs: Windows' own MaintainConnection reconnect never
            // restored a dropped sock, not once in seven drops, while our
            // explicit reopen did. So only give it a moment for a brief blip,
            // then reopen ourselves.
            _watchdog.Change(OsReconnectGrace, Timeout.InfiniteTimeSpan);
        }
        else if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected && _dropped)
        {
            _ = ResumeAfterOsReconnectAsync();
        }
    }

    private BluetoothLEPreferredConnectionParametersRequest? _paramsRequest;

    /// <summary>
    /// Right after connecting, the socks negotiate interval 25–30 ms with
    /// slave latency 10, i.e. they may sleep through ten connection events
    /// and listen only every ~330 ms. One of them then dropped after 42–96 s,
    /// every time, with or without heating: the pattern of a sleep clock
    /// drifting out of the receive window during the skipped events, not of
    /// a random radio fade. PowerOptimized asks for latency 0 at 90–180 ms
    /// (6 s timeout), so the sock has to listen at every event, about every
    /// 120 ms. The request only lasts while the returned object is alive.
    /// </summary>
    private void RequestLatencyZero()
    {
        if (_device is null || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        try
        {
            _paramsRequest?.Dispose();
            _paramsRequest = _device.RequestPreferredConnectionParameters(
                BluetoothLEPreferredConnectionParameters.PowerOptimized);
            Logf($"Requested latency-0 link parameters: {_paramsRequest.Status}");
        }
        catch (Exception ex) { Logf($"Link parameter request failed: {ex.Message}"); }
    }

    // A sock that renegotiates its link right before dropping would show up here.
    private void OnConnectionParametersChanged(BluetoothLEDevice sender, object args)
    {
        if (_writeChar is not null && !_dropped) LogLinkParameters("link changed");
    }

    private static readonly TimeSpan OsReconnectGrace = TimeSpan.FromSeconds(2);
    private DateTime _connectedAtUtc;

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
            _connectedAtUtc = DateTime.UtcNow;
            _batteryPollTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
            RequestLatencyZero();
            SetHealth(LinkHealth.Connected);
            ConnectionChanged?.Invoke(this, true);
            StartKeepAlive();
        }
        catch (Exception ex)
        {
            Logf($"Resume after reconnect failed: {ex.Message} — full reconnect.");
            _dropped = false;
            await TearDownDeviceAsync().ConfigureAwait(false);
            ScheduleReconnect(DateTime.MinValue);
        }
        finally { _connectLock.Release(); }
    }

    private void OnWatchdog()
    {
        if (!_dropped || _disposed || !_reconnectRequested) return;
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

    /// <param name="attemptStart">When the failed attempt began. The socks
    /// advertise only every 10–30 s and a Windows connect attempt waits ~8 s
    /// for one, so a slow failure is retried immediately: any pause is a
    /// window in which an advert can be missed. Only a fast failure (an error
    /// rather than a timeout) gets a pause, so it can't spin.</param>
    private void ScheduleReconnect(DateTime attemptStart)
    {
        if (!_reconnectRequested || _disposed) return;
        var delay = DateTime.UtcNow - attemptStart < TimeSpan.FromSeconds(2)
            ? TimeSpan.FromSeconds(3)
            : TimeSpan.FromMilliseconds(200);
        // Tied to the current binding: an unbind (e.g. a swap) cancels it, so
        // a retry planned for the old binding can't become a second retry
        // chain next to the new one.
        var bindToken = _bindCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, bindToken).ConfigureAwait(false); }
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
        if (SockProtocol.TryDecodeBatteryDeciVolts(data) is int dv && dv != _batteryDeciVolts)
        {
            _batteryDeciVolts = dv;
            BatteryChanged?.Invoke(this, SockProtocol.EstimatePercent(dv));
        }
    }

    private void ReleaseGattObjects()
    {
        if (_notifyChar is not null)
        {
            try { _notifyChar.ValueChanged -= OnNotify; } catch { }
            _notifyChar = null;
        }
        _writeChar = null;
        if (_gattService is not null)
        {
            try { _gattService.Dispose(); } catch { }
            _gattService = null;
        }
    }

    private async Task TearDownDeviceAsync()
    {
        _batteryPollTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        StopKeepAlive();
        _watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _dropped = false;
        // Unsubscribe on the sock only while the link is up: on a dropped link
        // this write would itself start an ~8 s connect attempt and delay the
        // reconnect by that much.
        if (_notifyChar is not null && _device?.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _notifyChar
                    .WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None)
                    .AsTask(cts.Token).ConfigureAwait(false);
            }
            catch { }
        }
        ReleaseGattObjects();
        if (_paramsRequest is not null)
        {
            try { _paramsRequest.Dispose(); } catch { }
            _paramsRequest = null;
        }
        if (_session is not null)
        {
            try { _session.MaintainConnection = false; } catch { }
            try { _session.Dispose(); } catch { }
            _session = null;
        }
        if (_device is not null)
        {
            try { _device.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                try { _device.ConnectionParametersChanged -= OnConnectionParametersChanged; } catch { }
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
        StopPresenceWatch();
        try { _batteryPollTimer.Dispose(); } catch { }
        try { _watchdog.Dispose(); } catch { }
        try { _keepAliveTimer.Dispose(); } catch { }
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
