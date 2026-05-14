namespace ReonOSC.Services;

/// <summary>
/// Stub for the Pebble Feel signal hook. The full implementation reads the
/// signal pixel out of the SteamVR compositor's mirror texture via OpenVR's
/// GetMirrorTextureD3D11 + a small D3D11 staging-copy. This file is kept
/// inert (Start/Stop are no-ops, SignalChanged never fires) until OpenVR
/// bindings are wired:
///
///   Option A — vendor ValveSoftware/openvr/headers/openvr_api.cs as a
///              Compile item, ship openvr_api.dll alongside the EXE.
///   Option B — pick a working OpenVR NuGet (OpenVR.NET et al.) and adapt
///              the call sites in TrySample().
///
/// Once bindings are present, swap this stub for the implementation in
/// git history (commit 6a59c88) — the surrounding plumbing (settings
/// toggle, bridge events, GUI checkbox) is already in place.
/// </summary>
public sealed class PfSignalReader : IDisposable
{
    public event EventHandler<PfSignalSample>? SignalChanged;
    public event EventHandler<string>? Log;

    public bool IsRunning => false;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(40);

    public void Start()
    {
        Log?.Invoke(this, "PF signal hook: OpenVR bindings not yet integrated. " +
                          "See PfSignalReader.cs for setup options.");
    }

    public void Stop() { /* no-op */ }
    public void Dispose() { /* no-op */ }

    // Reference SignalChanged so the compiler doesn't warn it's never raised.
    private void _Hint() => SignalChanged?.Invoke(this, PfSignalSample.None);
}

/// <summary>One RGB sample from the compositor mirror. (0,0,0) means "off / no signal".</summary>
public readonly record struct PfSignalSample(byte R, byte G, byte B)
{
    public static readonly PfSignalSample None = new(0, 0, 0);
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
