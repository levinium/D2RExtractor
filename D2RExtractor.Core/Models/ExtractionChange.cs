namespace D2RExtractor.Models;

/// <summary>What a run did to one file.</summary>
public enum ChangeKind
{
    /// <summary>The archives gained this file, or it was missing from the extraction.</summary>
    Added,

    /// <summary>The file existed and its contents were replaced.</summary>
    Updated,

    /// <summary>The archives no longer contain it, so it was deleted.</summary>
    Removed,
}

/// <summary>
/// One file a run wrote or deleted, as recorded for "what changed last time".
/// </summary>
/// <param name="Kind">Added, updated or removed.</param>
/// <param name="RelPath">Path relative to the target folder, e.g. <c>data\hd\items\misc\rune.dds</c>.</param>
/// <param name="Size">Size in bytes after the change, or the size it had before being removed.</param>
public readonly record struct ExtractionChange(ChangeKind Kind, string RelPath, long Size);

/// <summary>Which operation produced a change record.</summary>
public enum RunKind
{
    /// <summary>A full extraction into an empty target.</summary>
    Extract,

    /// <summary>A diff against the archives — the usual after a game patch.</summary>
    Update,
}

/// <summary>
/// The header for the last run's change list: enough to describe what happened without
/// reading the list itself.
///
/// <para>
/// Kept separate from <see cref="ExtractionManifest"/> because the manifest describes the
/// extraction as it now stands, while this describes one event that happened to it. Conflating
/// them would mean an update rewriting the whole manifest to record that it changed nothing.
/// </para>
/// </summary>
public class ExtractionRunRecord
{
    /// <summary>Sidecar holding the change list, beside the manifest.</summary>
    public const string DefaultChangeFile = ".extraction_changes.txt";

    /// <summary>When the run finished, in UTC.</summary>
    public DateTime RanAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether this was a fresh extraction or an update.</summary>
    public RunKind Kind { get; set; }

    public int FilesAdded { get; set; }
    public int FilesUpdated { get; set; }
    public int FilesRemoved { get; set; }
    public int FilesUnchanged { get; set; }
    public long BytesWritten { get; set; }

    /// <summary>
    /// Whether a per-file list was written alongside this record.
    ///
    /// <para>
    /// False for a fresh extraction, deliberately. Every one of its ~150,000 files is an addition,
    /// so the list would be a byte-for-byte second copy of the manifest's own sidecar and another
    /// ~11 MB of writes to say something the summary already says. The viewer reads the manifest
    /// for that case instead.
    /// </para>
    /// </summary>
    public bool HasChangeList { get; set; }

    /// <summary>True when the run was cancelled or failed partway.</summary>
    public bool WasInterrupted { get; set; }

    /// <summary>Total files the run touched, however it touched them.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public int TotalChanged => FilesAdded + FilesUpdated + FilesRemoved;
}
