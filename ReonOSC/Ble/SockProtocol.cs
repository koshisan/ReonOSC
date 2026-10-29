namespace ReonOSC.Ble;

/// <summary>
/// Protocol constants and frame builders for the "DRCODC" generic heated
/// socks (JieLi AC632N chip, FFF0 GATT service). Verified on real hardware
/// 2026-10-27 — see docs/heated-socks-protocol.md.
///
/// Frame layout:
///   host → sock: AA cmd len data… sum 55
///   sock → host: BB cmd len data… sum 55
///   sum = (cmd + len + sum(data)) &amp; 0xFF
/// </summary>
public static class SockProtocol
{
    /// <summary>Advertised local name both socks use. Not unique per device —
    /// L vs. R has to be told apart by MAC.</summary>
    public const string DeviceLocalName = "AC632N_1(BLE)";

    public static readonly Guid ServiceUuid = new("0000fff0-0000-1000-8000-00805f9b34fb");

    /// <summary>Notify characteristic. Carries battery reports and (so far
    /// unobserved) other state changes initiated by the sock side.</summary>
    public static readonly Guid CharNotify = new("0000fff1-0000-1000-8000-00805f9b34fb");

    /// <summary>Write-with-response command sink. Writes without response are
    /// silently dropped by the firmware.</summary>
    public static readonly Guid CharWrite  = new("0000fff2-0000-1000-8000-00805f9b34fb");

    public const byte FrameStartHost = 0xAA;
    public const byte FrameStartSock = 0xBB;
    public const byte FrameEnd       = 0x55;

    public const byte CmdSetTemp = 0x01;
    public const byte CmdTimer   = 0x02;
    public const byte CmdBattery = 0x04;

    /// <summary>Target temperature range in °C per vendor app. 0 means off.</summary>
    public const byte TempOff = 0;
    public const byte TempMin = 35;
    public const byte TempMax = 70;

    /// <summary>Build a host→sock frame with the given command byte and payload.</summary>
    public static byte[] BuildFrame(byte cmd, ReadOnlySpan<byte> data)
    {
        if (data.Length > 255) throw new ArgumentOutOfRangeException(nameof(data));
        var frame = new byte[4 + data.Length + 1];
        frame[0] = FrameStartHost;
        frame[1] = cmd;
        frame[2] = (byte)data.Length;
        for (int i = 0; i < data.Length; i++) frame[3 + i] = data[i];
        int sum = cmd + data.Length;
        for (int i = 0; i < data.Length; i++) sum += data[i];
        frame[3 + data.Length] = (byte)(sum & 0xFF);
        frame[^1] = FrameEnd;
        return frame;
    }

    /// <summary>Set target temperature in °C, or 0 to turn heating off.
    /// Values other than 0 are clamped to [TempMin, TempMax].</summary>
    public static byte[] BuildSetTemperature(byte tempC)
    {
        if (tempC != 0)
        {
            if (tempC < TempMin) tempC = TempMin;
            else if (tempC > TempMax) tempC = TempMax;
        }
        return BuildFrame(CmdSetTemp, new[] { tempC });
    }

    /// <summary>Set auto-off timer in minutes, 0 = no timer. The vendor app
    /// sends 0 for anything ≤ 15.</summary>
    public static byte[] BuildTimer(byte minutes) =>
        BuildFrame(CmdTimer, new[] { minutes });

    public static byte[] BuildBatteryQuery() =>
        BuildFrame(CmdBattery, ReadOnlySpan<byte>.Empty);

    /// <summary>Parse a notify frame coming from the sock. Returns the
    /// battery voltage in tenths of a volt (e.g. 35 = 3.5 V) if this is a
    /// battery report, else null. The byte is not a percentage: over an hour
    /// of heating it fell 39 → 34, jittering ±1 as the heater switched,
    /// while the cuff showed 1–2 of 4 battery LEDs. Non-battery notifies are
    /// ignored — the vendor app only logs them too.</summary>
    public static int? TryDecodeBatteryDeciVolts(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6) return null;
        if (frame[0] != FrameStartSock || frame[^1] != FrameEnd) return null;
        if (frame[1] != CmdSetTemp) return null;   // battery reports echo cmd=0x01
        if (frame[2] != 1) return null;
        byte deciVolts = frame[3];
        // Checksum sanity — malformed frames would be a wire issue worth
        // catching rather than reporting a bogus value.
        int sum = (frame[1] + frame[2] + frame[3]) & 0xFF;
        if (sum != frame[4]) return null;
        // A single Li-ion cell lives between ~2.5 and 4.3 V.
        if (deciVolts < 25 || deciVolts > 45) return null;
        return deciVolts;
    }

    /// <summary>State-of-charge estimate from the reported voltage, using the
    /// same five steps as the vendor app's battery bar (read from its code),
    /// so ReonOSC shows what the app shows.</summary>
    public static int EstimatePercent(int deciVolts) => deciVolts switch
    {
        >= 39 => 100,
        >= 37 => 75,
        >= 35 => 50,
        >= 30 => 25,
        _ => 0,
    };
}
