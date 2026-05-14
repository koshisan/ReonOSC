using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReonOSC.Ble;

/// <summary>
/// Persistent storage for the Reon bond token, sharing location with the Python
/// tool (<c>%APPDATA%\reon\token.json</c>) so pairing once works for both.
/// </summary>
public static class TokenStorage
{
    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "reon");

    public static string TokenPath => Path.Combine(ConfigDir, "token.json");

    public sealed class StoredToken
    {
        [JsonPropertyName("mac")]
        public string Mac { get; set; } = "";

        [JsonPropertyName("auth_token_hex")]
        public string AuthTokenHex { get; set; } = "";

        [JsonPropertyName("paired_at")]
        public string PairedAt { get; set; } = "";

        public byte[] TokenBytes => Convert.FromHexString(AuthTokenHex);
    }

    public static StoredToken? Load()
    {
        if (!File.Exists(TokenPath)) return null;
        try
        {
            var json = File.ReadAllText(TokenPath);
            return JsonSerializer.Deserialize<StoredToken>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string mac, byte[] token)
    {
        Directory.CreateDirectory(ConfigDir);
        var stored = new StoredToken
        {
            Mac = mac,
            AuthTokenHex = Convert.ToHexString(token).ToLowerInvariant(),
            PairedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        };
        var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(TokenPath, json);
    }
}
