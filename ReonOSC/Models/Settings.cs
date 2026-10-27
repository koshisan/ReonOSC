using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReonOSC.Models;

/// <summary>
/// User-configurable settings persisted as JSON next to the bond token in
/// <c>%APPDATA%\reon\reonosc.json</c>. Lives alongside the Python tool's
/// token file deliberately so a single directory holds everything Reon-related.
/// </summary>
public sealed class Settings
{
    public int OscPort { get; set; } = 9302;

    /// <summary>Wire level (0..3) to apply when PFHotHigh OSC bool turns true.</summary>
    public int HeatTouchLevel { get; set; } = 3;

    /// <summary>Wire level (0..3) to apply when ChairOSC water bool turns true.</summary>
    public int ColdWaterLevel { get; set; } = 3;

    public bool StartMinimised { get; set; } = false;
    public bool AutoConnectOnStart { get; set; } = true;

    /// <summary>Enable the experimental hook that reads the Pebble Feel signal
    /// pixel from the SteamVR compositor mirror texture. Requires SteamVR running.</summary>
    public bool EnablePfSignalHook { get; set; } = false;

    /// <summary>Persisted window size. Zero means "use computed default".</summary>
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }

    /// <summary>Last-known Reon BLE MAC. Used for fast reconnect; otherwise we scan by name.</summary>
    public string? LastKnownMac { get; set; }

    // OSC address templates. Configurable so users on VRChat ("/avatar/parameters/...")
    // or different rigs can rewrite them without recompiling.
    public string AddrPfHotHigh { get; set; } = "/PFHotHigh";
    public string AddrWater     { get; set; } = "/ChairOSC/v1/water";
    public string AddrCold      { get; set; } = "/ChairOSC/v1/cold";
    public string AddrHeat      { get; set; } = "/ChairOSC/v1/heat";
    /// <summary>Wind isn't actionable by the Reon (no fan) but it's a useful
    /// environmental signal to forward to Home Assistant via MQTT so the
    /// user can drive a physical fan off it.</summary>
    public string AddrWind      { get; set; } = "/ChairOSC/v1/wind";
    /// <summary>Per-foot touch inputs — only drive the socks, one each.</summary>
    public string AddrTouchL    { get; set; } = "/ChairOSC/v1/touch/left";
    public string AddrTouchR    { get; set; } = "/ChairOSC/v1/touch/right";

    // -------- heated socks --------
    // Known MACs from the paired devices (persist across restarts so we can
    // reconnect without a scan — see docs/heated-socks-protocol.md).
    public string? SockLMac { get; set; }
    public string? SockRMac { get; set; }

    /// <summary>Idle baseline temperature when nothing else is requested.
    /// Shared by both feet.</summary>
    public bool SockBaselineOn { get; set; } = true;
    public int  SockBaseline   { get; set; } = 30;

    /// <summary>Target temperature while PFHotHigh is active.</summary>
    public int SockHeatTouch { get; set; } = 55;

    /// <summary>Max temperature the heat float maps to when non-zero.
    /// The resolver is a step function, not linear — the socks are too
    /// thermally lazy for sub-max values to matter, and frequent writes
    /// risk overloading them.</summary>
    public int SockHeatMax { get; set; } = 60;

    /// <summary>Max temperature a touchL/R float maps to when non-zero.</summary>
    public int SockTouchMax { get; set; } = 45;

    /// <summary>Hard safety limit, °C. Clamped to the firmware's 70 °C ceiling
    /// in the resolver. Lowering this at runtime drags HeatTouch/HeatMax/
    /// TouchMax down with it in the GUI.</summary>
    public int SockLimit { get; set; } = 60;

    /// <summary>Manual-override targets, °C, per foot. Only consulted when
    /// <see cref="ControlService.ManualOverride"/> is on.</summary>
    public int ManualSockL { get; set; } = 40;
    public int ManualSockR { get; set; } = 40;

    // MQTT publish — pushes the current state (mode/level/source, connection,
    // telemetry) to a broker so Home Assistant can mirror the device and react
    // (e.g. turn on the AC when we go into Cool).
    public bool MqttEnabled { get; set; } = false;
    public string MqttHost { get; set; } = "";
    public int MqttPort { get; set; } = 1883;
    public string MqttUsername { get; set; } = "";
    public string MqttPassword { get; set; } = "";
    /// <summary>Root topic. State lives at <c>{base}/state</c>, availability at
    /// <c>{base}/availability</c>.</summary>
    public string MqttBaseTopic { get; set; } = "reonosc";
    /// <summary>Prefix for Home Assistant MQTT Discovery. Empty string disables
    /// discovery (HA users on a non-default prefix can also retarget here).</summary>
    public string MqttDiscoveryPrefix { get; set; } = "homeassistant";

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "reon");

    public static string SettingsPath => Path.Combine(ConfigDir, "reonosc.json");

    public static Settings Load()
    {
        if (!File.Exists(SettingsPath)) return new Settings();
        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }
        catch
        {
            return new Settings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
