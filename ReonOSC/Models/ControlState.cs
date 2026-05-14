namespace ReonOSC.Models;

/// <summary>The four OSC-driven input sources, each with a value and a timestamp.</summary>
public enum InputSource { PfHotHigh, Water, Cold, Heat }

public sealed class OscInputs
{
    private readonly float[] _values = new float[4];
    private readonly DateTime[] _changedAt = new DateTime[4];

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
/// </summary>
public static class ControlResolver
{
    public static ResolvedCommand Resolve(OscInputs inputs, Settings settings)
    {
        // Heat candidates: PfHotHigh (gives configured Heat-Touch level when "on") and the heat float (mapped).
        int heatLevel = 0;
        DateTime heatTs = DateTime.MinValue;

        if (inputs.Get(InputSource.PfHotHigh) >= 0.5f)
        {
            heatLevel = Math.Max(heatLevel, ClampLevel(settings.HeatTouchLevel));
            heatTs = Latest(heatTs, inputs.GetChangedAt(InputSource.PfHotHigh));
        }
        var heatFromFloat = FloatToLevel(inputs.Get(InputSource.Heat));
        if (heatFromFloat is { } hl)
        {
            heatLevel = Math.Max(heatLevel, hl);
            heatTs = Latest(heatTs, inputs.GetChangedAt(InputSource.Heat));
        }

        // Cool candidates
        int coolLevel = 0;
        DateTime coolTs = DateTime.MinValue;

        if (inputs.Get(InputSource.Water) >= 0.5f)
        {
            coolLevel = Math.Max(coolLevel, ClampLevel(settings.ColdWaterLevel));
            coolTs = Latest(coolTs, inputs.GetChangedAt(InputSource.Water));
        }
        var coolFromFloat = FloatToLevel(inputs.Get(InputSource.Cold));
        if (coolFromFloat is { } cl)
        {
            coolLevel = Math.Max(coolLevel, cl);
            coolTs = Latest(coolTs, inputs.GetChangedAt(InputSource.Cold));
        }

        if (heatLevel == 0 && coolLevel == 0)
            return ResolvedCommand.Stop;

        if (heatLevel > 0 && coolLevel > 0)
            return heatTs >= coolTs
                ? new ResolvedCommand(Ble.ReonProtocol.Mode.Heat, heatLevel)
                : new ResolvedCommand(Ble.ReonProtocol.Mode.Cool, coolLevel);

        return heatLevel > 0
            ? new ResolvedCommand(Ble.ReonProtocol.Mode.Heat, heatLevel)
            : new ResolvedCommand(Ble.ReonProtocol.Mode.Cool, coolLevel);
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
