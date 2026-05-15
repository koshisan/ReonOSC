using ReonOSC.Ble;
using ReonOSC.Models;
using ReonOSC.Osc;

namespace ReonOSC.Services;

/// <summary>
/// The heart of the app: receives OSC messages, applies the manual-override
/// flag from the GUI, resolves to a single (mode, level) command, and writes
/// to the connected ReonClient with a debounce so we don't spam the radio.
/// </summary>
public sealed class ControlService : IAsyncDisposable
{
    public ReonClient Reon { get; } = new();
    public OscServer Osc { get; } = new();
    public PfSignalReader PfSignal { get; } = new();
    public Settings Settings { get; private set; }
    private readonly OscInputs _inputs = new();

    /// <summary>If true, OSC inputs are ignored and the GUI's manual mode/level is used.</summary>
    public bool ManualOverride { get; set; }
    public ResolvedCommand ManualCommand { get; set; } = ResolvedCommand.Stop;

    public ResolvedCommand LastSentCommand { get; private set; } = ResolvedCommand.Stop;
    public DateTime LastSentAt { get; private set; }
    /// <summary>Which input source authored the last sent command — used by the
    /// GUI's SOURCE indicator. One of "Manual", "PF", "OSC".</summary>
    public string LastCommandSource { get; private set; } = "OSC";
    /// <summary>Granular trigger string — distinguishes which specific input
    /// fired ("OSC:PFHotHigh", "OSC:water", "PFSignal", "Manual", "Idle"). Used
    /// by the MQTT publisher so HA automations can tell e.g. an avatar caress
    /// (OSC:PFHotHigh — transient) from sitting at a fire (PFSignal — durable).</summary>
    public string LastCommandReason { get; private set; } = "Idle";

    public event EventHandler<ResolvedCommand>? CommandSent;
    public event EventHandler<string>? Log;
    /// <summary>Fires whenever any OSC input value changes. Snapshot keys match
    /// the GUI contract: "PFHotHigh", "water", "cold", "heat".</summary>
    public event EventHandler<IReadOnlyDictionary<string, float>>? InputsChanged;
    /// <summary>Fires when (cmd, source, reason) changes — regardless of
    /// whether a BLE write actually happened. MQTT consumers listen here so
    /// they can publish reason changes even when no Reon is connected.</summary>
    public event EventHandler<(ResolvedCommand cmd, string source, string reason)>? StateChanged;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TimeSpan _minWriteGap = TimeSpan.FromMilliseconds(120);

    public ControlService(Settings settings)
    {
        Settings = settings;
        Osc.MessageReceived += OnOscMessage;
        Osc.Log += (_, msg) => Log?.Invoke(this, msg);
        Reon.Log += (_, msg) => Log?.Invoke(this, msg);
        PfSignal.Log += (_, msg) => Log?.Invoke(this, msg);

        // Plug the PFSignal reader directly into the resolver: every time the
        // decoded thermal state changes (e.g. user walks into a heat zone),
        // we trigger a reconcile and the next target picks PF over OSC.
        //
        // Temporal smoothing: require the same decoded state across N
        // consecutive samples before propagating it. At ~25 Hz, N=3 is
        // ~120 ms — well below the perceptual threshold for thermal change
        // but above the typical single-frame jitter window from MSAA /
        // supersampling / lens-distortion sub-pixel shifts. Without this
        // the level was flipping mid-frame on edge motion in VR.
        PfSignal.SignalChanged += (_, sample) =>
        {
            var next = PfSignalDecoder.Decode(sample);
            if (next == _pfCandidate)
            {
                _pfCandidateCount++;
            }
            else
            {
                _pfCandidate = next;
                _pfCandidateCount = 1;
            }

            if (_pfCandidateCount >= PfStabilityFrames && next != _pfState)
            {
                _pfState = next;
                _ = ReconcileAsync();
            }
        };
    }

    private PfThermalState _pfState = PfThermalState.Off;
    private PfThermalState _pfCandidate = PfThermalState.Off;
    private int _pfCandidateCount;
    private const int PfStabilityFrames = 3;

    public async ValueTask DisposeAsync()
    {
        Osc.Dispose();
        PfSignal.Dispose();
        await Reon.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    public void ApplySettings(Settings settings)
    {
        Settings = settings;
        if (Osc.IsRunning && Osc.Port != settings.OscPort)
        {
            Osc.Stop();
            Osc.Start(settings.OscPort);
        }
    }

    public void StartOsc() => Osc.Start(Settings.OscPort);

    public void StopOsc() => Osc.Stop();

    public float GetInput(InputSource src) => _inputs.Get(src);

    /// <summary>Cumulative count of OSC packets we've received this session
    /// regardless of whether the address matched anything. Useful as a
    /// liveness signal without spamming the log on chatty senders.</summary>
    public long OscPacketsReceived { get; private set; }

    /// <summary>Set to true after the first OSC packet of the session arrives,
    /// so we emit exactly one log line confirming the pipe is live.</summary>
    private bool _firstOscLogged;

    /// <summary>Address of the first OSC packet received this session, or null
    /// if none yet. Surfaced via the bridge so the user always sees this even
    /// when the first packet arrived before the GUI was up.</summary>
    public string? FirstOscAddress { get; private set; }
    public string? FirstOscArg { get; private set; }

    /// <summary>Per-address rolling stats so the user can inspect which OSC
    /// addresses their sender actually emits without flooding the log.
    /// Capped to avoid pathological growth on senders that include random
    /// IDs in every path.</summary>
    public readonly record struct OscAddressStats(long Count, string? LastArg, DateTime LastSeenUtc);
    private const int MaxTrackedAddresses = 1024;
    private readonly Dictionary<string, OscAddressStats> _addrStats = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, OscAddressStats> DiscoveredAddresses => _addrStats;

    private void OnOscMessage(object? sender, OscMessage msg)
    {
        OscPacketsReceived++;

        // Update per-address stats for the inspector. Bounded so a chatty
        // sender that puts random IDs in every path can't blow up memory.
        var argStr = msg.Arguments.Count == 0 ? null
                   : msg.Arguments[0] is null ? null
                   : msg.Arguments[0]!.ToString();
        if (_addrStats.TryGetValue(msg.Address, out var prev))
            _addrStats[msg.Address] = new OscAddressStats(prev.Count + 1, argStr, DateTime.UtcNow);
        else if (_addrStats.Count < MaxTrackedAddresses)
            _addrStats[msg.Address] = new OscAddressStats(1, argStr, DateTime.UtcNow);

        if (!_firstOscLogged)
        {
            _firstOscLogged = true;
            var argHint = argStr ?? "(no args)";
            FirstOscAddress = msg.Address;
            FirstOscArg = argHint;
            Log?.Invoke(this, $"OSC first packet: {msg.Address} = {argHint}");
        }

        var addr = msg.Address;
        if (MatchAddress(addr, Settings.AddrPfHotHigh))
        {
            if (msg.TryGetBool(0, out var b)) UpdateInput(InputSource.PfHotHigh, b ? 1f : 0f);
        }
        else if (MatchAddress(addr, Settings.AddrWater))
        {
            if (msg.TryGetBool(0, out var b)) UpdateInput(InputSource.Water, b ? 1f : 0f);
        }
        else if (MatchAddress(addr, Settings.AddrCold))
        {
            if (msg.TryGetFloat(0, out var f)) UpdateInput(InputSource.Cold, Math.Clamp(f, 0f, 1f));
        }
        else if (MatchAddress(addr, Settings.AddrHeat))
        {
            if (msg.TryGetFloat(0, out var f)) UpdateInput(InputSource.Heat, Math.Clamp(f, 0f, 1f));
        }
        // Unmatched addresses are intentionally silent — the packet counter
        // above proves the pipe works, and the OSC inputs panel in the GUI
        // surfaces matched values directly.
    }

    /// <summary>
    /// Compare with permissive matching. Three accepted shapes:
    ///   1. Direct equality (modulo a leading-slash difference).
    ///   2. The incoming address has the standard VRChat avatar prefix
    ///      '/avatar/parameters/' tacked onto the configured one — i.e. the
    ///      user types '/PFHotHigh' in the GUI and VRChat actually sends
    ///      '/avatar/parameters/PFHotHigh'. We strip the prefix and re-match.
    ///      The prefix is intentionally invisible in the GUI to keep the
    ///      field labels short.
    /// </summary>
    private const string VrcAvatarPrefix = "avatar/parameters/";

    private static bool MatchAddress(string incoming, string configured)
    {
        if (string.Equals(incoming, configured, StringComparison.Ordinal)) return true;

        var a = incoming.TrimStart('/');
        var b = configured.TrimStart('/');
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;

        // Implicit VRChat avatar-param prefix: the configured path is treated
        // as "name within /avatar/parameters/".
        if (a.StartsWith(VrcAvatarPrefix, StringComparison.Ordinal))
        {
            var tail = a.Substring(VrcAvatarPrefix.Length);
            if (string.Equals(tail, b, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private void UpdateInput(InputSource src, float value)
    {
        if (!_inputs.Set(src, value)) return;
        InputsChanged?.Invoke(this, Snapshot());
        _ = ReconcileAsync();
    }

    /// <summary>Snapshot of the current OSC inputs by GUI-facing key.</summary>
    public IReadOnlyDictionary<string, float> Snapshot() => new Dictionary<string, float>
    {
        ["PFHotHigh"] = _inputs.Get(InputSource.PfHotHigh),
        ["water"]     = _inputs.Get(InputSource.Water),
        ["cold"]      = _inputs.Get(InputSource.Cold),
        ["heat"]      = _inputs.Get(InputSource.Heat),
    };

    /// <summary>
    /// Resolve the current target across all input sources.
    /// Priority: ManualOverride > PF signal (when active) > OSC inputs.
    /// PF levels (1..4) are clamped to the connected device's per-direction
    /// max (e.g. CoolFastHigh → Cool L3 on an RNP-3 which caps cool at 3).
    /// </summary>
    private (ResolvedCommand cmd, string source, string reason) ResolveTarget()
    {
        if (ManualOverride) return (ManualCommand, "Manual", "Manual");

        if (_pfState.Mode != PfThermalMode.Off)
        {
            var mode = _pfState.Mode == PfThermalMode.Hot ? ReonProtocol.Mode.Heat : ReonProtocol.Mode.Cool;
            var caps = Reon.Capabilities;
            var max  = mode == ReonProtocol.Mode.Heat ? caps.HeatLevelMax : caps.CoolLevelMax;
            int level = Math.Clamp(_pfState.Level, 0, max);
            return (new ResolvedCommand(mode, level), "PF", "PFSignal");
        }

        var (cmd, oscReason) = ControlResolver.Resolve(_inputs, Settings);
        return (cmd, "OSC", oscReason);
    }

    /// <summary>Resolve the current target and push it to the device if it
    /// differs from the last sent. Also fires <see cref="StateChanged"/>
    /// whenever (cmd, source, reason) shifts — regardless of whether a BLE
    /// write happened — so MQTT can publish reason changes even when the
    /// device isn't connected.</summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var (target, source, reason) = ResolveTarget();

        // Reason-only changes update the property and fire StateChanged even
        // when the device is disconnected or the BLE write would be a no-op.
        bool stateShifted = target != LastSentCommand
                         || source != LastCommandSource
                         || reason != LastCommandReason;

        if (!Reon.IsConnected)
        {
            if (stateShifted)
            {
                LastCommandReason = reason;
                // Note: we deliberately do NOT update LastSentCommand/Source
                // here — those track what the device actually received.
                StateChanged?.Invoke(this, (target, source, reason));
            }
            return;
        }

        if (target == LastSentCommand && source == LastCommandSource)
        {
            if (reason != LastCommandReason)
            {
                LastCommandReason = reason;
                StateChanged?.Invoke(this, (target, source, reason));
            }
            return;
        }

        if (!await _writeLock.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            // simple debounce: rate-limit writes to ~8 per second
            var elapsed = DateTime.UtcNow - LastSentAt;
            if (elapsed < _minWriteGap)
                await Task.Delay(_minWriteGap - elapsed, ct).ConfigureAwait(false);

            // re-evaluate after the wait so we don't send a stale target
            (target, source, reason) = ResolveTarget();
            if (target == LastSentCommand && source == LastCommandSource)
            {
                if (reason != LastCommandReason)
                {
                    LastCommandReason = reason;
                    StateChanged?.Invoke(this, (target, source, reason));
                }
                return;
            }

            try
            {
                switch (target.Mode)
                {
                    case ReonProtocol.Mode.Cool: await Reon.SetCoolAsync(target.Level, ct).ConfigureAwait(false); break;
                    case ReonProtocol.Mode.Heat: await Reon.SetHeatAsync(target.Level, ct).ConfigureAwait(false); break;
                    default: await Reon.StopAsync(ct).ConfigureAwait(false); break;
                }
                LastSentCommand = target;
                LastSentAt = DateTime.UtcNow;
                LastCommandSource = source;
                LastCommandReason = reason;
                CommandSent?.Invoke(this, target);
                StateChanged?.Invoke(this, (target, source, reason));
            }
            catch (Exception ex)
            {
                Log?.Invoke(this, $"Write failed: {ex.Message}");
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

}
