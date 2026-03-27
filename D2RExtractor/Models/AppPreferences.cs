namespace D2RExtractor.Models;

/// <summary>
/// User-configurable application-level settings.
/// Stored at %AppData%\D2RExtractor\preferences.json — separate from the installations list.
/// </summary>
public class AppPreferences
{
    /// <summary>
    /// When true, the locales CASC prefix is extracted in addition to the base prefixes.
    /// This includes international voice dubbing audio files.
    /// </summary>
    public bool ExtractInternationalFiles { get; set; } = false;
}
