using D2RExtractor.Services.Updates;
using Shouldly;
using Xunit;

namespace D2RExtractor.Tests;

/// <summary>
/// Whether a published release is one to offer someone.
///
/// <para>
/// Acting on this replaces the running executable, so the interesting cases are the ones where the
/// feed says something odd. Every one of them is reproducible here without a network, which is the
/// entire reason the decision was separated from the fetching.
/// </para>
/// </summary>
public class UpdateDecisionTests
{
    private static readonly ReleaseAsset Zip =
        new("D2RExtractor-Compiled-Standalone_v1.1.9.zip", "https://example/x.zip", 66_000_000, "sha256:abc");

    private static ReleaseInfo Release(
        string tag, bool draft = false, bool pre = false, ReleaseAsset? asset = null) =>
        new(tag, "https://example/release", "notes", asset ?? Zip, draft, pre);

    [Fact]
    public void ANewerReleaseIsOfferedAndCanBeInstalled()
    {
        var verdict = UpdateDecision.For("1.1.8", Release("D2RExtractor_v1.1.9"));

        verdict.Outcome.ShouldBe(UpdateOutcome.Available);
        verdict.IsAvailable.ShouldBeTrue();
        verdict.CanInstall.ShouldBeTrue();
        verdict.Version.ToString().ShouldBe("1.1.9");
    }

    [Theory]
    [InlineData("1.1.8", "D2RExtractor_v1.1.8")]
    [InlineData("1.1.8", "D2RExtractor_v1.1.7")]
    [InlineData("2.0.0", "D2RExtractor_v1.9.9")]
    public void TheSameOrAnOlderReleaseIsNotOffered(string running, string published)
    {
        UpdateDecision.For(running, Release(published)).Outcome.ShouldBe(UpdateOutcome.UpToDate);
    }

    /// <summary>
    /// A draft is not published at all, and someone running a finished release has said, by running
    /// it, that they are not looking for a release candidate.
    /// </summary>
    [Fact]
    public void ADraftIsNeverOffered()
    {
        UpdateDecision.For("1.1.8", Release("D2RExtractor_v1.1.9", draft: true))
            .Outcome.ShouldBe(UpdateOutcome.UpToDate);
    }

    [Fact]
    public void APreReleaseIsNeverOffered()
    {
        UpdateDecision.For("1.1.8", Release("D2RExtractor_v1.1.9", pre: true))
            .Outcome.ShouldBe(UpdateOutcome.UpToDate);
    }

    /// <summary>
    /// A tag that says "-rc1" means the same thing whether or not anybody remembered to tick the
    /// pre-release box on the release page.
    /// </summary>
    [Fact]
    public void ATagThatNamesItselfAReleaseCandidateIsRefusedEvenIfUnlabelled()
    {
        UpdateDecision.For("1.1.8", Release("D2RExtractor_v1.1.9-rc1"))
            .Outcome.ShouldBe(UpdateOutcome.UpToDate);
    }

    /// <summary>
    /// Uncertainty resolves to Unknown, never to either confident answer. Unknown offers nothing
    /// and claims nothing, which is the only honest reading of a tag nobody could parse.
    /// </summary>
    [Fact]
    public void AnUnreadableTagIsUnknownRatherThanAnUpdate()
    {
        UpdateDecision.For("1.1.8", Release("D2RExtractor")).Outcome.ShouldBe(UpdateOutcome.Unknown);
    }

    [Fact]
    public void AnUnreadableRunningVersionIsUnknownRatherThanAnUpdate()
    {
        UpdateDecision.For("not-a-version", Release("D2RExtractor_v1.1.9"))
            .Outcome.ShouldBe(UpdateOutcome.Unknown);
    }

    [Fact]
    public void NoAnswerFromTheFeedIsUnknown()
    {
        UpdateDecision.For("1.1.8", null).Outcome.ShouldBe(UpdateOutcome.Unknown);
    }

    /// <summary>
    /// A release with no zip attached is still worth announcing — it just has to send the person to
    /// the page rather than offering to install something that is not there.
    /// </summary>
    [Fact]
    public void AReleaseWithNoZipIsAnnouncedButNotInstallable()
    {
        var verdict = UpdateDecision.For(
            "1.1.8", new ReleaseInfo("D2RExtractor_v1.1.9", "https://example/r", "notes", Asset: null));

        verdict.IsAvailable.ShouldBeTrue();
        verdict.CanInstall.ShouldBeFalse();
    }

    /// <summary>
    /// The asset is what the installer downloads and checks, so it has to survive the decision
    /// intact — a dropped digest would mean installing an unverified file.
    /// </summary>
    [Fact]
    public void TheAssetSurvivesTheDecision()
    {
        var verdict = UpdateDecision.For("1.1.8", Release("D2RExtractor_v1.1.9"));

        verdict.Asset.ShouldNotBeNull();
        verdict.Asset!.Digest.ShouldBe("sha256:abc");
        verdict.Asset.Size.ShouldBe(66_000_000);
    }
}
