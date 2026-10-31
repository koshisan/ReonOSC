using System.Text.Json;

namespace ReonOSC.Ble;

/// <summary>
/// A sock reached through the sockbridge over MQTT instead of local BLE.
/// The bridge holds the BLE link; this side only sends targets and mirrors
/// the bridge's state.
/// </summary>
public sealed class MqttSockLink : ISockLink
{
    private readonly SockBridgeConnection _conn;
    private readonly string _tag;
    private readonly System.Threading.Timer _heartbeat;
    private readonly object _gate = new();

    private ulong? _address;
    private string? _id;
    private bool _sockConnected;
    private int? _deciVolts;
    private byte? _target;
    private bool _wasConnected;

    // The bridge switches a sock off when it hears nothing for 120 s, so the
    // current target is repeated well within that.
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    public MqttSockLink(SockClient.Side side, SockBridgeConnection conn)
    {
        _conn = conn;
        _tag = side == SockClient.Side.Left ? "SockL" : "SockR";
        _heartbeat = new System.Threading.Timer(_ => _ = HeartbeatAsync(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _conn.StateReceived += OnState;
        _conn.AvailabilityChanged += _ => RaiseConnectionIfChanged();
    }

    public ulong? Address => _address;
    public bool IsConnected => _conn.BridgeOnline && _sockConnected && _id is not null;
    public double? BatteryVolts => _deciVolts is int dv ? dv / 10.0 : null;
    public int? BatteryPercent => _deciVolts is int dv ? SockProtocol.EstimatePercent(dv) : null;
    public SockClient.LinkHealth Health { get; private set; } = SockClient.LinkHealth.Unbound;
    public int DeadManMinutes { get; set; }

    public event EventHandler<int>? BatteryChanged;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<SockClient.LinkHealth>? HealthChanged;
    public event EventHandler<string>? Log;

    public Task BindAsync(ulong address, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _address = address;
            _id = SockBridgeConnection.IdFor(address);
            _sockConnected = false;
            _deciVolts = null;
        }
        Logf($"Bound to {SockClient.FormatMac(address)} via sockbridge.");
        SetHealth(SockClient.LinkHealth.Connecting);
        if (_conn.LastState(_id) is JsonElement s) OnState(_id, s);
        _heartbeat.Change(HeartbeatInterval, HeartbeatInterval);
        return Task.CompletedTask;
    }

    public async Task UnbindAsync()
    {
        // Unbinding means "don't drive this sock any more": switch it off
        // rather than leave the bridge holding the last target until its
        // command timeout.
        if (_id is string id) await _conn.PublishTargetAsync(id, 0).ConfigureAwait(false);
        _heartbeat.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        lock (_gate)
        {
            _address = null;
            _id = null;
            _target = null;
            _sockConnected = false;
            _deciVolts = null;
        }
        SetHealth(SockClient.LinkHealth.Unbound);
        RaiseConnectionIfChanged();
    }

    public async Task<bool> SetTemperatureAsync(byte tempC, CancellationToken ct = default)
    {
        byte normalised = tempC == 0 ? (byte)0
                        : (byte)Math.Clamp(tempC, SockProtocol.TempMin, SockProtocol.TempMax);
        var id = _id;
        if (id is null) return false;
        // The resolver calls this every second with the same value; the
        // heartbeat repeats it, so only changes go out here.
        if (_target == normalised) return IsConnected;
        _target = normalised;
        var ok = await _conn.PublishTargetAsync(id, normalised).ConfigureAwait(false);
        Logf(normalised == 0 ? "Set OFF." : $"Set {normalised} °C.");
        return ok && IsConnected;
    }

    public async Task<bool> TurnOffAsync(CancellationToken ct = default)
    {
        _target = 0;
        return _id is string id && await _conn.PublishTargetAsync(id, 0).ConfigureAwait(false);
    }

    private async Task HeartbeatAsync()
    {
        if (_id is string id && _target is byte t)
            await _conn.PublishTargetAsync(id, t).ConfigureAwait(false);
    }

    private void OnState(string id, JsonElement s)
    {
        if (id != _id) return;
        bool connected = s.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True;
        int? dv = s.TryGetProperty("battery_v", out var b) && b.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(b.GetDouble() * 10) : null;
        var health = (s.TryGetProperty("health", out var h) ? h.GetString() : null) switch
        {
            "connected" => SockClient.LinkHealth.Connected,
            "absent" => SockClient.LinkHealth.Absent,
            _ => SockClient.LinkHealth.Connecting,
        };

        _sockConnected = connected;
        if (dv is int v && v != _deciVolts)
        {
            _deciVolts = v;
            BatteryChanged?.Invoke(this, SockProtocol.EstimatePercent(v));
        }
        SetHealth(health);
        RaiseConnectionIfChanged();
    }

    private void RaiseConnectionIfChanged()
    {
        var now = IsConnected;
        if (now == _wasConnected) return;
        _wasConnected = now;
        Logf(now ? "Connected (via sockbridge)." : "Disconnected (via sockbridge).");
        if (now && _target is byte t && _id is string id)
            _ = _conn.PublishTargetAsync(id, t);   // bridge may have just restarted
        ConnectionChanged?.Invoke(this, now);
    }

    private void SetHealth(SockClient.LinkHealth h)
    {
        if (h == Health) return;
        Health = h;
        HealthChanged?.Invoke(this, h);
    }

    private void Logf(string msg) => Log?.Invoke(this, $"[{_tag}] {msg}");

    public async ValueTask DisposeAsync()
    {
        _heartbeat.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try { await TurnOffAsync().ConfigureAwait(false); } catch { }
        _conn.StateReceived -= OnState;
        _heartbeat.Dispose();
    }
}
