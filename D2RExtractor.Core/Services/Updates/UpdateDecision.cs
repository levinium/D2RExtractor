namespace D2RExtractor.Services.Updates;

/// <summary>What a check concluded.</summary>
public enum UpdateOutcome
{
    /// <summary>Nothing newer is published.</summary>
    UpToDate,

    /// <summary>There is a newer release, and it is worth saying so.</summary>
    Available,

    /// <summary>The question could not be answered — offline, refused, unreadable.</summary>
    Unknown,
}

/// <summary>
/// The downloadable file attached to a release.
/// </summary>
/// <param name="Name">The file name, e.g. "D2RExtractor-Compiled-Standalone_v1.1.7.zip".</param>
/// <param name="Url">Where to fetch it from.</param>
/// <param name="Size">Its length in bytes, for the progress bar and a cheap sanity check.</param>
/// <param name="Digest">
/// The checksum GitHub recorded when it was uploaded, as "sha256:hex". Null on
/// older assets, which predate the field.
/// </param>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Digest);

/// <summary>
/// A published release, reduced to what the decision needs.
/// </summary>
/// <param name="Tag">The tag or release name, e.g. "D2RExtractor_v1.1.7".</param>
/// <param name="Url">Where a person goes to get it by hand.</param>
/// <param name="Notes">The release body, shown so nobody has to accept an unexplained update.</param>
/// <param name="Asset">The zip to install, or null when the release has none.</param>
/// <param name="IsDraft">Unpublished, and visible only to the people who wrote it.</param>
/// <param name="IsPreRelease">Marked as not ready for general use.</param>
public sealed record ReleaseInfo(
    string? Tag,
    string? Url,
    string? Notes = null,
    ReleaseAsset? Asset = null,
    bool IsDraft = false,
    bool IsPreRelease = false);

/// <summary>The conclusion, and everything needed to act on it.</summary>
public readonly record struct UpdateVerdict(
    UpdateOutcome Outcome,
    ReleaseVersion Version,
    string? Url,
    string? Notes = null,
    ReleaseAsset? Asset = null)
{
    public bool IsAvailable => Outcome == UpdateOutcome.Available;

    /// <summary>
    /// Whether this update can be installed by the app rather than by hand. A
    /// release with no zip attached is still worth announcing — it just has to
    /// send the person to the page.
    /// </summary>
    public bool CanInstall => IsAvailable && Asset is not null;
}

/// <summary>
/// Whether a published release is one to tell the user about.
/// <para>
/// Separated from the fetching so it can be exercised against every awkward
/// answer a release feed can give — a draft, a release candidate, a tag nobody
/// can parse, a version older than the one already running — none of which need
/// a network to reproduce.
/// </para>
/// </summary>
public static class UpdateDecision
{
    /// <summary>
    /// Compares what is running against what is published.
    /// </summary>
    /// <param name="currentVersion">This build's version.</param>
    /// <param name="latest">The newest published release, or null if none could be read.</param>
    /// <remarks>
    /// <para>
    /// Every uncertain case resolves to <see cref="UpdateOutcome.Unknown"/>
    /// rather than to either confident answer. An update prompt that fires on a
    /// version string it could not read offers people a download they may
    /// already have; a false "up to date" is the quieter failure but it is still
    /// a lie. Saying so plainly is the only honest answer when the input did not
    /// parse — and it matters more here than in most apps, because acting on the
    /// answer replaces the running executable.
    /// </para>
    /// <para>
    /// Drafts and pre-releases are never offered. A draft is not published at
    /// all, and someone running a finished release has said, by doing so, that
    /// they are not looking for a release candidate.
    /// </para>
    /// </remarks>
    public static UpdateVerdict For(string? currentVersion, ReleaseInfo? latest)
    {
        if (latest is null) return new UpdateVerdict(UpdateOutcome.Unknown, default, null);

        if (latest.IsDraft || latest.IsPreRelease)
            return new UpdateVerdict(UpdateOutcome.UpToDate, default, null);

        if (!ReleaseVersion.TryParse(latest.Tag, out var published))
            return new UpdateVerdict(UpdateOutcome.Unknown, default, null);

        // A tag that parses but is itself a pre-release is refused too, even
        // when the feed did not label it — "v1.1.0-rc1" means the same thing
        // whether or not anyone ticked the box.
        if (published.IsPreRelease)
            return new UpdateVerdict(UpdateOutcome.UpToDate, default, null);

        if (!ReleaseVersion.TryParse(currentVersion, out var running))
            return new UpdateVerdict(UpdateOutcome.Unknown, published, latest.Url, latest.Notes);

        return published.IsNewerThan(running)
            ? new UpdateVerdict(UpdateOutcome.Available, published, latest.Url, latest.Notes, latest.Asset)
            : new UpdateVerdict(UpdateOutcome.UpToDate, published, latest.Url, latest.Notes);
    }
}
