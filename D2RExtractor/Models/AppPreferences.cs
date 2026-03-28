namespace D2RExtractor.Models;

/// <summary>
/// User preferences persisted to %AppData%\D2RExtractor\preferences.json.
/// </summary>
public class AppPreferences
{
    /// <summary>Whether to extract international audio/dubbing files (locales folder).</summary>
    public bool ExtractInternationalFiles { get; set; }
}
