namespace ReonOSC.Ble;

/// <summary>
/// Sony Reon Pocket 3 BLE protocol constants and frame builders.
/// Mirrors the Python <c>reon.protocol</c> module.
/// </summary>
public static class ReonProtocol
{
    /// <summary>
    /// Advertised name prefix used during discovery. Today's wearable Reon
    /// ships as "RNP-3"; future generations are expected to keep the "RNP-"
    /// prefix. Note that the wire protocol below is only verified on RNP-3.
    /// </summary>
    public const string DeviceName = "RNP-3";  // legacy alias; prefer DeviceNamePrefix
    public const string DeviceNamePrefix = "RNP-";

    // UUID group 3 is `404e`, NOT `4057` — easy to misread.
    public static readonly Guid ServiceUuid = new("04ca1501-fd57-404e-8459-c5ef8d765c8d");
    public static readonly Guid CharAuth    = new("04ca150a-fd57-404e-8459-c5ef8d765c8d");
    public static readonly Guid CharCmd     = new("04ca1503-fd57-404e-8459-c5ef8d765c8d");
    public static readonly Guid CharTelem   = new("04ca1581-fd57-404e-8459-c5ef8d765c8d");
    public static readonly Guid CharStatus  = new("04ca1584-fd57-404e-8459-c5ef8d765c8d");

    public const int AuthTokenLength = 17;
    public const int CommandFrameLength = 12;
    public const int LevelMin = 0;
    /// <summary>Absolute upper bound across all known models; per-direction
    /// caps live in <see cref="DeviceCapabilities"/>.</summary>
    public const int LevelMaxAbsolute = 4;
    /// <summary>Backwards-compat alias preserved for callers that don't yet
    /// honour per-device capabilities. New code should query the device's
    /// <see cref="DeviceCapabilities"/> instead.</summary>
    public const int LevelMax = LevelMaxAbsolute;

    /// <summary>Standard BLE Device Information service — model number characteristic.</summary>
    public static readonly Guid ModelNumberUuid = new("00002a24-0000-1000-8000-00805f9b34fb");

    public enum Mode : byte
    {
        Cool = 0x01,
        Heat = 0x02,
        Smart = 0x03,
        Stop = 0x04,
    }

    public static byte[] BuildCommand(Mode mode, int level = 0)
    {
        if (mode != Mode.Stop && (level < LevelMin || level > LevelMaxAbsolute))
            throw new ArgumentOutOfRangeException(nameof(level),
                $"Level {level} out of range {LevelMin}..{LevelMaxAbsolute}.");

        var buf = new byte[CommandFrameLength];
        buf[3] = (byte)mode;
        buf[4] = (byte)level;
        return buf;
    }

    private static float? ReadTemp(byte hi, byte lo)
    {
        int v = (hi << 8) | lo;
        return v == 0xffff ? null : v / 100f;
    }

    /// <summary>
    /// Decode the first 4 sensor fields from a CharTelem notify frame
    /// (big-endian int16 in 1/100 °C). 0xFFFF indicates an unwired slot
    /// and is returned as null. RNP-P1 emits more sensors after the first
    /// 4 (humidity etc.) but they're not currently surfaced.
    /// </summary>
    public static Telemetry? DecodeTelemetry(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9) return null;
        return new Telemetry(
            Board:     ReadTemp(data[1], data[2]),
            SkinPlate: ReadTemp(data[3], data[4]),
            Heatsink:  ReadTemp(data[5], data[6]),
            Ambient:   ReadTemp(data[7], data[8])
        );
    }

    /// <summary>Decode the state-notify echoed on CharCmd after each command.</summary>
    public static StateEcho? DecodeStateEcho(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5) return null;
        return new StateEcho(data[3], data[4]);
    }

    /// <summary>Friendly translation of vendor-defined ATT errors seen on the Reon.</summary>
    public static string? ExplainAttError(byte code) => code switch
    {
        0x80 => "invalid value (e.g. level out of range)",
        0x81 => "authentication failed (wrong bond token on 150a)",
        0x83 => "authorization missing (no token written this connection)",
        _ => null,
    };
}

public readonly record struct Telemetry(float? Board, float? SkinPlate, float? Heatsink, float? Ambient);

public readonly record struct StateEcho(byte Mode, byte Level);

/// <summary>Per-model command-range capabilities.</summary>
public sealed record DeviceCapabilities(int CoolLevelMax, int HeatLevelMax)
{
    public static readonly DeviceCapabilities Default = new(3, 3);

    public static DeviceCapabilities ForModel(string? model) =>
        (model ?? "").Trim() switch
        {
            "RNP-3"  => new DeviceCapabilities(3, 3),
            "RNP-P1" => new DeviceCapabilities(4, 3),
            _ => Default,
        };
}
