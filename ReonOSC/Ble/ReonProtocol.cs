namespace ReonOSC.Ble;

/// <summary>
/// Sony Reon Pocket 3 BLE protocol constants and frame builders.
/// Mirrors the Python <c>reon.protocol</c> module.
/// </summary>
public static class ReonProtocol
{
    public const string DeviceName = "RNP-3";

    // UUID group 3 is `404e`, NOT `4057` — easy to misread.
    public static readonly Guid ServiceUuid = new("04ca1501-fd57-404e-8459-c5ef8d765c8d");
    public static readonly Guid CharAuth    = new("04ca150a-fd57-404e-8459-c5ef8d765c8d");
    public static readonly Guid CharCmd     = new("04ca1503-fd57-404e-8459-c5ef8d765c8d");
    public static readonly Guid CharTelem   = new("04ca1581-fd57-404e-8459-c5ef8d765c8d");
    public static readonly Guid CharStatus  = new("04ca1584-fd57-404e-8459-c5ef8d765c8d");

    public const int AuthTokenLength = 17;
    public const int CommandFrameLength = 12;
    public const int LevelMin = 0;
    public const int LevelMax = 3;

    public enum Mode : byte
    {
        Cool = 0x01,
        Heat = 0x02,
        Smart = 0x03,
        Stop = 0x04,
    }

    public static byte[] BuildCommand(Mode mode, int level = 0)
    {
        if (mode != Mode.Stop && (level < LevelMin || level > LevelMax))
            throw new ArgumentOutOfRangeException(nameof(level),
                $"Level {level} out of range {LevelMin}..{LevelMax}.");

        var buf = new byte[CommandFrameLength];
        buf[3] = (byte)mode;
        buf[4] = (byte)level;
        return buf;
    }

    /// <summary>
    /// Decode the 4 plate temperatures from a CharTelem notify frame
    /// (4 × big-endian int16 in 1/100 °C, with empirical channel labels).
    /// </summary>
    public static Telemetry? DecodeTelemetry(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9) return null;
        return new Telemetry(
            Board:     ((data[1] << 8) | data[2]) / 100f,
            SkinPlate: ((data[3] << 8) | data[4]) / 100f,
            Heatsink:  ((data[5] << 8) | data[6]) / 100f,
            Ambient:   ((data[7] << 8) | data[8]) / 100f
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

public readonly record struct Telemetry(float Board, float SkinPlate, float Heatsink, float Ambient);

public readonly record struct StateEcho(byte Mode, byte Level);
