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
    public int OscPort { get; set; } = 9001;

    /// <summary>Wire level (0..3) to apply when PFHotHigh OSC bool turns true.</summary>
    public int HeatTouchLevel { get; set; } = 3;

    /// <summary>Wire level (0..3) to apply when ChairOSC water bool turns true.</summary>
    public int ColdWaterLevel { get; set; } = 3;

    public bool StartMinimised { get; set; } = false;
    public bool AutoConnectOnStart { get; set; } = true;

    /// <summary>Last-known Reon BLE MAC. Used for fast reconnect; otherwise we scan by name.</summary>
    public string? LastKnownMac { get; set; }

    // OSC address templates. Configurable so users on VRChat ("/avatar/parameters/...")
    // or different rigs can rewrite them without recompiling.
    public string AddrPfHotHigh { get; set; } = "/PFHotHigh";
    public string AddrWater     { get; set; } = "/ChairOSC/v1/water";
    public string AddrCold      { get; set; } = "/ChairOSC/v1/cold";
    public string AddrHeat      { get; set; } = "/ChairOSC/v1/heat";

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
