namespace D2RExtractor.Models;

/// <summary>
/// User preferences persisted to %AppData%\D2RExtractor\preferences.json.
/// </summary>
public class AppPreferences
{
    /// <summary>Whether to extract international audio/dubbing files (locales folder).</summary>
    public bool ExtractInternationalFiles { get; set; }

    /// <summary>
    /// The language code to extract (e.g. "itIT", "deDE").
    /// Locale files are extracted into the data\ tree, replacing the base English audio/text.
    /// </summary>
    public string? InternationalLanguage { get; set; }

    /// <summary>
    /// Whether Update should also checksum every extracted file against its recorded content key,
    /// rather than trusting the recorded key plus the file's size on disk.
    ///
    /// <para>
    /// Off by default: it reads and hashes the entire extraction (tens of GB), which takes several
    /// minutes. It catches files that were corrupted or edited outside the app — cases the normal
    /// comparison cannot see because the file's size did not change. It never causes extra writes.
    /// </para>
    /// </summary>
    public bool VerifyFileContents { get; set; }

    /// <summary>Available language codes and their display names.</summary>
    public static readonly (string Code, string Name)[] AvailableLanguages =
    [
        ("deDE", "Deutsch (German)"),
        ("enUS", "English"),
        ("esES", "Espa\u00f1ol (Spanish)"),
        ("esMX", "Espa\u00f1ol (Latin America)"),
        ("frFR", "Fran\u00e7ais (French)"),
        ("itIT", "Italiano (Italian)"),
        ("jaJP", "\u65e5\u672c\u8a9e (Japanese)"),
        ("koKR", "\ud55c\uad6d\uc5b4 (Korean)"),
        ("plPL", "Polski (Polish)"),
        ("ptBR", "Portugu\u00eas (Brazilian)"),
        ("ruRU", "\u0420\u0443\u0441\u0441\u043a\u0438\u0439 (Russian)"),
        ("zhCN", "\u4e2d\u6587 (Simplified Chinese)"),
        ("zhTW", "\u4e2d\u6587 (Traditional Chinese)"),
    ];
}
