using Newtonsoft.Json;

namespace D2RExtractor.Models;

/// <summary>
/// One extracted file as recorded in the manifest.
/// </summary>
/// <param name="RelPath">Path relative to the installation folder, e.g. <c>data\global\allcofs.bin</c>.</param>
/// <param name="Key">
/// Content key the file was extracted from (see <see cref="KeySource"/>), or null for entries
/// written by v1.1.6 and earlier, which recorded paths only.
/// </param>
/// <param name="Size">Size in bytes at extraction time, or -1 when unknown (legacy entries).</param>
public readonly record struct ManifestEntry(string RelPath, string? Key, long Size);

/// <summary>
/// Persisted record of a completed extraction for one D2R installation.
///
/// <para>
/// Stored as two files in <c>&lt;D2RPath&gt;\data\</c>:
/// <list type="bullet">
///   <item><c>.extraction_manifest.json</c> — this header, a few hundred bytes.</item>
///   <item><c>.extraction_files.txt</c> — one tab-separated record per extracted file.</item>
/// </list>
/// They are split because the header changes often (every flush, and again at the end of an
/// update) while the file list only grows. Keeping the list out of the JSON turns each of those
/// header updates from a multi-megabyte rewrite into a ~400-byte one; see
/// <see cref="Services.ManifestService"/> for the write path.
/// </para>
///
/// Used to enumerate and delete extracted files during Undo, and to diff against the game
/// archives during Update.
/// </summary>
public class ExtractionManifest
{
    /// <summary>Sidecar file name used by schema version 2.</summary>
    public const string DefaultEntryFile = ".extraction_files.txt";

    /// <summary>Current schema version written by this build.</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// Schema version. 2 = header + sidecar with per-file content keys.
    /// <para>
    /// IMPORTANT: Do NOT add a property initializer. Manifests written by v1.1.6 and earlier have
    /// no such field, and must deserialise to 0 so they are recognised as legacy path-only
    /// manifests (same sentinel technique as <see cref="InternationalExtracted"/>).
    /// </para>
    /// </summary>
    public int ManifestVersion { get; set; }

    /// <summary>UTC timestamp when the extraction completed.</summary>
    public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Total bytes of extracted content this manifest accounts for.</summary>
    public long TotalBytesExtracted { get; set; }

    /// <summary>
    /// True when the extraction completed successfully.
    /// Written as false before the first file is extracted and set to true only on successful
    /// completion. Old manifests without this field deserialize to true (backward compat).
    /// </summary>
    public bool IsComplete { get; set; } = true;

    /// <summary>
    /// True when the international (locales) CASC prefix was extracted.
    /// Null means this manifest was created before v1.01 — treat as false (not extracted).
    /// IMPORTANT: Do NOT add a property initializer (= false or = null).
    /// The field must deserialise as null when absent from old JSON so the sentinel works.
    /// </summary>
    public bool? InternationalExtracted { get; set; }

    /// <summary>
    /// The language code that was extracted (e.g. "itIT"). Null if no language was extracted
    /// or the manifest predates language-aware extraction.
    /// </summary>
    public string? InternationalLanguage { get; set; }

    /// <summary>
    /// Which <see cref="Models.KeySource"/> the sidecar's content keys came from. Keys are only
    /// comparable against keys of the same source, so an update that finds a different source
    /// (e.g. the install moved from Battle.net to Steam) falls back to size-only comparison.
    /// </summary>
    public string? KeySource { get; set; }

    /// <summary>Number of records in the sidecar. Informational — the sidecar itself is authoritative.</summary>
    public int EntryCount { get; set; }

    /// <summary>Name of the sidecar file, relative to the manifest's own folder.</summary>
    public string EntryFile { get; set; } = DefaultEntryFile;

    /// <summary>
    /// True when the keys in the sidecar were assumed from the archives rather than verified
    /// against the extracted files — set when migrating a legacy manifest without a verify pass.
    /// A file that changed without changing size could still be stale in that case.
    /// </summary>
    public bool KeysBackfilledBySize { get; set; }

    /// <summary>
    /// Legacy (schema version &lt; 2) file list: relative paths only, no keys or sizes.
    /// Retained so manifests written by v1.1.6 and earlier still deserialise; never written by
    /// this build. Read it through <see cref="Services.ManifestService.EnumerateEntries"/>, which
    /// presents both schemas uniformly.
    /// </summary>
    public List<string>? ExtractedFiles { get; set; }

    /// <summary>True for a manifest written by v1.1.6 or earlier (paths only, no content keys).</summary>
    [JsonIgnore]
    public bool IsLegacySchema => ManifestVersion < CurrentVersion;

    /// <summary>Newtonsoft hook: keep the legacy list out of anything this build writes.</summary>
    public bool ShouldSerializeExtractedFiles() => false;
}
