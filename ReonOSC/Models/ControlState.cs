namespace ReonOSC.Models;

/// <summary>The OSC-driven input sources, each with a value and a timestamp.
/// Wind is forwarded to MQTT but is NOT consulted by the Reon resolver — the
/// device has no fan, so wind is purely passthrough.</summary>
public enum InputSource { PfHotHigh, Water, Cold, Heat, Wind }

public sealed class OscInputs
{
    private readonly float[] _values = new float[5];
    private readonly DateTime[] _changedAt = new DateTime[5];

    public float Get(InputSource src) => _values[(int)src];

    public DateTime GetChangedAt(InputSource src) => _changedAt[(int)src];

    public bool Set(InputSource src, float value)
    {
        int idx = (int)src;
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
    /// Map a 0..1 float to wire level 0..3 by quartile, with 0.0 (exactly) acting
    /// as a dead zone returning null (no contribution from this source).
    /// </summary>
    public static int? FloatToLevel(float v)
    {
        if (v <= 0f) return null;
        if (v <= 0.25f) return 0;
        if (v <= 0.50f) return 1;
        if (v <= 0.75f) return 2;
        return 3;
    }

    private static int ClampLevel(int level) =>
        Math.Clamp(level, Ble.ReonProtocol.LevelMin, Ble.ReonProtocol.LevelMax);

    private static DateTime Latest(DateTime a, DateTime b) => a >= b ? a : b;
}
