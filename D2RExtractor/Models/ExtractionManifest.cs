namespace D2RExtractor.Models;

/// <summary>
/// Persisted record of a completed extraction for one D2R installation.
/// Stored at: &lt;D2RPath&gt;\data\.extraction_manifest.json
/// Used to enumerate and delete extracted files during Undo.
/// </summary>
public class ExtractionManifest
{
    /// <summary>UTC timestamp when the extraction completed.</summary>
    public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Relative file paths (relative to the D2R installation folder) of every file
    /// that was extracted from CASC storage.
    /// Example: "data\global\ui\Loading\loadingscreen.dc6"
    /// </summary>
    public List<string> ExtractedFiles { get; set; } = new();

    /// <summary>Total bytes extracted.</summary>
    public long TotalBytesExtracted { get; set; }

    /// <summary>
    /// True when the extraction completed successfully.
    /// Written as false during extraction (on every periodic flush) and set to true only on
    /// successful completion. Old manifests without this field deserialize to true (backward compat).
    /// </summary>
    public bool IsComplete { get; set; } = true;
}
