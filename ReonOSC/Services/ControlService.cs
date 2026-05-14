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

    public event EventHandler<ResolvedCommand>? CommandSent;
    public event EventHandler<string>? Log;
    /// <summary>Fires whenever any OSC input value changes. Snapshot keys match
    /// the GUI contract: "PFHotHigh", "water", "cold", "heat".</summary>
    public event EventHandler<IReadOnlyDictionary<string, float>>? InputsChanged;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TimeSpan _minWriteGap = TimeSpan.FromMilliseconds(120);

    public ControlService(Settings settings)
    {
        Settings = settings;
        Osc.MessageReceived += OnOscMessage;
        Osc.Log += (_, msg) => Log?.Invoke(this, msg);
        Reon.Log += (_, msg) => Log?.Invoke(this, msg);
        PfSignal.Log += (_, msg) => Log?.Invoke(this, msg);
    }

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

    private void OnOscMessage(object? sender, OscMessage msg)
    {
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
    }

    /// <summary>Compare with permissive matching: tolerate missing or extra leading slash.</summary>
    private static bool MatchAddress(string incoming, string configured)
    {
        if (string.Equals(incoming, configured, StringComparison.Ordinal)) return true;
        if (incoming.StartsWith('/') ^ configured.StartsWith('/'))
        {
            var a = incoming.TrimStart('/');
            var b = configured.TrimStart('/');
            return string.Equals(a, b, StringComparison.Ordinal);
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

    /// <summary>Resolve the current target and push it to the device if it differs from the last sent.</summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        if (!Reon.IsConnected) return;

        var target = ManualOverride
            ? ManualCommand
            : ControlResolver.Resolve(_inputs, Settings);

        if (target == LastSentCommand) return;

        if (!await _writeLock.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            // simple debounce: rate-limit writes to ~8 per second
            var elapsed = DateTime.UtcNow - LastSentAt;
            if (elapsed < _minWriteGap)
                await Task.Delay(_minWriteGap - elapsed, ct).ConfigureAwait(false);

            // re-evaluate after the wait so we don't send a stale target
            target = ManualOverride
                ? ManualCommand
                : ControlResolver.Resolve(_inputs, Settings);
            if (target == LastSentCommand) return;

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
                CommandSent?.Invoke(this, target);
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
