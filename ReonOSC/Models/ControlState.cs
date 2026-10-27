namespace ReonOSC.Models;

/// <summary>The OSC-driven input sources, each with a value and a timestamp.
/// Wind is forwarded to MQTT but is NOT consulted by the Reon resolver — the
/// device has no fan, so wind is purely passthrough. TouchL/R only drive the
/// socks, one per foot.</summary>
public enum InputSource { PfHotHigh, Water, Cold, Heat, Wind, TouchL, TouchR }

public sealed class OscInputs
{
    private const int Count = 7;
    private readonly float[] _values = new float[Count];
    private readonly DateTime[] _changedAt = new DateTime[Count];
    /// <summary>Last time this input was observed with a positive value.
    /// Used by the sock resolver for off-hysteresis — brief drops to 0 are
    /// held active for a window before actually turning the heating off.</summary>
    private readonly DateTime[] _lastActiveAt = new DateTime[Count];

    public float Get(InputSource src) => _values[(int)src];

    public DateTime GetChangedAt(InputSource src) => _changedAt[(int)src];

    public DateTime GetLastActiveAt(InputSource src) => _lastActiveAt[(int)src];

    public bool Set(InputSource src, float value)
    {
        int idx = (int)src;
        if (value > 0f) _lastActiveAt[idx] = DateTime.UtcNow;
        if (Math.Abs(_values[idx] - value) < 0.0001f) return false;
        _values[idx] = value;
        _changedAt[idx] = DateTime.UtcNow;
        return true;
    }
}

public readonly record struct ResolvedCommand(Ble.ReonProtocol.Mode Mode, int Level)
{
    public static readonly ResolvedCommand Stop = new(Ble.ReonProtocol.Mode.Stop, 0);
}

/// <summary>
/// Resolves the four OSC inputs plus configured preset levels into a single
/// (mode, level) the device should be in. Pure function of state — no I/O.
///
/// The companion 'reason' string identifies the specific trigger so HA can
/// automate against it — e.g. "OSC:PFHotHigh" (avatar caress, ignore) vs
/// "PFSignal" (in a world heat zone, do crank the heating). The reason is
/// not part of <see cref="ResolvedCommand"/> because mode/level equality
/// must continue to gate BLE writes; reason changes only feed telemetry.
/// </summary>
public static class ControlResolver
{
    public static (ResolvedCommand cmd, string reason) Resolve(OscInputs inputs, Settings settings)
    {
        // Heat candidates: PfHotHigh (gives configured Heat-Touch level when "on") and the heat float (mapped).
        int heatLevel = 0;
        DateTime heatTs = DateTime.MinValue;
        string? heatReason = null;

        if (inputs.Get(InputSource.PfHotHigh) >= 0.5f)
        {
            var lvl = ClampLevel(settings.HeatTouchLevel);
            if (lvl > heatLevel) { heatLevel = lvl; heatReason = "OSC:PFHotHigh"; }
            heatTs = Latest(heatTs, inputs.GetChangedAt(InputSource.PfHotHigh));
        }
        var heatFromFloat = FloatToLevel(inputs.Get(InputSource.Heat));
        if (heatFromFloat is { } hl)
        {
            if (hl > heatLevel) { heatLevel = hl; heatReason = "OSC:heat"; }
            heatTs = Latest(heatTs, inputs.GetChangedAt(InputSource.Heat));
        }

        // Cool candidates
        int coolLevel = 0;
        DateTime coolTs = DateTime.MinValue;
        string? coolReason = null;

        if (inputs.Get(InputSource.Water) >= 0.5f)
        {
            var lvl = ClampLevel(settings.ColdWaterLevel);
            if (lvl > coolLevel) { coolLevel = lvl; coolReason = "OSC:water"; }
            coolTs = Latest(coolTs, inputs.GetChangedAt(InputSource.Water));
        }
        var coolFromFloat = FloatToLevel(inputs.Get(InputSource.Cold));
        if (coolFromFloat is { } cl)
        {
            if (cl > coolLevel) { coolLevel = cl; coolReason = "OSC:cold"; }
            coolTs = Latest(coolTs, inputs.GetChangedAt(InputSource.Cold));
        }

        if (heatLevel == 0 && coolLevel == 0)
            return (ResolvedCommand.Stop, "Idle");

        if (heatLevel > 0 && coolLevel > 0)
        {
            return heatTs >= coolTs
                ? (new ResolvedCommand(Ble.ReonProtocol.Mode.Heat, heatLevel), heatReason ?? "OSC")
                : (new ResolvedCommand(Ble.ReonProtocol.Mode.Cool, coolLevel), coolReason ?? "OSC");
        }

        return heatLevel > 0
            ? (new ResolvedCommand(Ble.ReonProtocol.Mode.Heat, heatLevel), heatReason ?? "OSC")
            : (new ResolvedCommand(Ble.ReonProtocol.Mode.Cool, coolLevel), coolReason ?? "OSC");
    }

    /// <summary>
    /// Map a 0..1 float to wire level 1..4 by quartile, with 0.0 (exactly) acting
    /// as a dead zone returning null (no contribution from this source). Each
    /// quartile that's actually non-zero claims at least L1 — the previous
    /// scheme returned L0 for (0, 0.25] which contributed nothing to the
    /// resolver's max-pick and made small float inputs silently inert. The
    /// per-direction cap clamp happens later in <see cref="ControlService.ResolveTarget"/>.
    /// </summary>
    public static int? FloatToLevel(float v)
    {
        if (v <= 0f)    return null;
        if (v <= 0.25f) return 1;
        if (v <= 0.50f) return 2;
        if (v <= 0.75f) return 3;
        return 4;
    }

    private static int ClampLevel(int level) =>
        Math.Clamp(level, Ble.ReonProtocol.LevelMin, Ble.ReonProtocol.LevelMax);

    private static DateTime Latest(DateTime a, DateTime b) => a >= b ? a : b;
}

/// <summary>Target state for a single sock: a wire temperature (0 = off,
/// otherwise 35..70 °C) plus the source label the UI shows next to the zone.
/// Equality is used by <see cref="Services.ControlService"/> to decide
/// whether a BLE write is needed.</summary>
public readonly record struct SockTarget(byte TempC, string Source)
{
    public static readonly SockTarget Off = new(0, "Off");
}

/// <summary>
/// Resolves per-foot sock targets. Pure function of state. Each OSC input is
/// treated as a step function — the socks are too thermally lazy for linear
/// scaling to buy anything, and frequent writes risk overloading them — plus
/// an off-hysteresis window so brief drops to 0 don't bounce the heating.
/// Rules per the user:
///   value &gt; 0        → source counts as active immediately (its full max)
///   value == 0 ≥ 10s → source counts as inactive
///
/// Candidates per foot (whichever contributes the highest wire temp wins):
///   BaselineOn active       → baseline            source="Baseline"
///   PFHotHigh active        → heatTouch           source="Heat"
///   heat float active       → sockHeatMax         source="Heat"
///   touchL/R float active   → touchMax            source="Touch"
/// If none active and baseline off → Off (0, "Off"). Target is clamped to
/// SockLimit before leaving the resolver.
/// </summary>
public static class SockResolver
{
    public static readonly TimeSpan HysteresisWindow = TimeSpan.FromSeconds(10);

    public enum Foot { Left, Right }

    public static SockTarget Resolve(Foot foot, OscInputs inputs, Settings settings, DateTime nowUtc)
    {
        int limit = ClampLimit(settings.SockLimit);
        int best = 0;
        string source = "Off";

        void Consider(int candidate, string label)
        {
            // Priority on tie: Manual > Heat > Touch > Baseline. The resolver
            // itself doesn't deal with Manual (the caller substitutes it),
            // so between Heat/Touch/Baseline we only need a lexical tiebreak
            // defined by first-wins order of insertion below. Keep it simple:
            // strict greater-than, so Heat always edges out an equal-value
            // Baseline because it's considered first.
            if (candidate > best)
            {
                best = candidate;
                source = label;
            }
        }

        if (IsActive(inputs, InputSource.PfHotHigh, nowUtc))
            Consider(settings.SockHeatTouch, "Heat");
        if (IsActive(inputs, InputSource.Heat, nowUtc))
            Consider(settings.SockHeatMax, "Heat");
        var touchSrc = foot == Foot.Left ? InputSource.TouchL : InputSource.TouchR;
        if (IsActive(inputs, touchSrc, nowUtc))
            Consider(settings.SockTouchMax, "Touch");
        if (settings.SockBaselineOn)
            Consider(settings.SockBaseline, "Baseline");

        if (best <= 0) return SockTarget.Off;
        int clamped = Math.Clamp(best, 0, limit);
        return new SockTarget((byte)clamped, source);
    }

    public static SockTarget ResolveManual(int manualTempC, Settings settings)
    {
        int limit = ClampLimit(settings.SockLimit);
        int c = Math.Clamp(manualTempC, 0, limit);
        if (c <= 0) return SockTarget.Off;
        return new SockTarget((byte)c, "Manual");
    }

    private static bool IsActive(OscInputs inputs, InputSource src, DateTime nowUtc)
    {
        if (inputs.Get(src) > 0f) return true;
        var lastOn = inputs.GetLastActiveAt(src);
        if (lastOn == default) return false;
        return (nowUtc - lastOn) < HysteresisWindow;
    }

    /// <summary>Hard-cap the configured limit at the sock firmware's max
    /// (70 °C). Nothing in the UI should allow more, but defence in depth —
    /// a corrupted settings file must never translate to a 90 °C write.</summary>
    public static int ClampLimit(int limit) =>
        Math.Clamp(limit, 0, Ble.SockProtocol.TempMax);
}
