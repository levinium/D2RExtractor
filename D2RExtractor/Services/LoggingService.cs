using System;
using System.IO;

namespace D2RExtractor.Services;

/// <summary>
/// Writes a persistent session log to %AppData%\D2RExtractor\session.log.
/// The log file is cleared at each application launch so only the most recent
/// session is retained — useful for post-crash diagnosis.
/// All methods swallow IO errors silently so logging never crashes the app.
/// </summary>
public static class LoggingService
{
    private static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "D2RExtractor");

    public static readonly string LogPath = Path.Combine(LogDir, "session.log");

    /// <summary>
    /// Call once at application startup.
    /// Creates (or truncates) the session log file.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            // Overwrite / clear previous session
            File.WriteAllText(LogPath, $"[D2R Extractor] Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        }
        catch { /* never crash the app over logging */ }
    }

    /// <summary>Appends a timestamped line to the session log.</summary>
    public static void Write(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {message}\n");
        }
        catch { }
    }
}
