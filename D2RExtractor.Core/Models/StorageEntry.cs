namespace D2RExtractor.Models;

/// <summary>
/// One file as it exists inside the game storage, as produced by an extraction backend's
/// enumeration pass.
/// </summary>
/// <param name="VirtualPath">
/// Storage virtual path in the CascLib namespace form, e.g. <c>data:data\global\allcofs.bin</c>.
/// Both backends produce identical paths.
/// </param>
/// <param name="FileSize">Decoded size of the file in bytes.</param>
/// <param name="ContentKey">
/// Lower-case hex identity of the file's content, or null when the backend could not supply one
/// (in which case the extractor falls back to size-only comparison — see <see cref="KeySource"/>).
/// Harvested during enumeration, so it costs nothing extra.
/// </param>
public readonly record struct StorageEntry(string VirtualPath, ulong FileSize, string? ContentKey);

/// <summary>
/// Where a manifest's per-file content keys came from. Keys are only comparable against keys of the
/// same source, so this is recorded in the manifest and re-checked on every update: if an install
/// switches storage format (Battle.net ↔ Steam), the stored keys are meaningless against the new
/// storage and the extractor falls back to size-only comparison.
/// </summary>
public static class KeySource
{
    /// <summary>No key available — size-only comparison.</summary>
    public const string None = "none";

    /// <summary>CascLib CASC_FIND_DATA.CKey — the MD5 of the decoded file content.</summary>
    public const string CascCKey = "casc-ckey";

    /// <summary>Steam text-ROOT md5 field — also the MD5 of the decoded file content.</summary>
    public const string SteamRootMd5 = "steam-root-md5";

    /// <summary>Steam TVFS encoding keys, joined across spans. Identifies the stored blob, not the content.</summary>
    public const string SteamEKey = "steam-ekey";

    /// <summary>
    /// True when a key of this source is the MD5 of the file's decoded content, and can therefore be
    /// compared directly against a hash of the extracted file on disk without touching the archives.
    /// </summary>
    public static bool IsContentMd5(string? source) =>
        source is CascCKey or SteamRootMd5;
}
