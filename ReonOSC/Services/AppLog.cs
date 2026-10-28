using System.IO;
using ReonOSC.Models;

namespace ReonOSC.Services;

/// <summary>Append-only runtime log at %APPDATA%\reon\reonosc.log, so log lines
/// survive beyond the UI's in-memory buffer. Rotates to .1 once on startup when
/// the file is over 2 MB. Best-effort: never throws.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    public static string LogPath => Path.Combine(Settings.ConfigDir, "reonosc.log");

    static AppLog()
    {
        try
        {
            Directory.CreateDirectory(Settings.ConfigDir);
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                File.Move(LogPath, LogPath + ".1", overwrite: true);
        }
        catch { }
    }

    public static void Write(string kind, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{kind}] {msg}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(LogPath, line); } catch { }
        }
    }
}
