using D2RExtractor.Services;
using D2RExtractor.Services.Updates;
using Shouldly;
using Xunit;

namespace D2RExtractor.Tests;

/// <summary>
/// Reading GitHub's answer about the latest release.
///
/// <para>
/// The document is read by hand rather than deserialized into a type, because it carries dozens of
/// fields this app has no interest in and binding to them would turn an unrelated change upstream
/// into a parse failure here. That choice is only safe if the hand-reading is pinned.
/// </para>
/// </summary>
public class ReleaseFeedTests
{
    /// <summary>A trimmed copy of what the real feed returns for this project.</summary>
    private const string RealShape = """
        {
          "tag_name": "D2RExtractor_v1.1.7",
          "name": "D2RExtractor v1.1.7",
          "draft": false,
          "prerelease": false,
          "html_url": "https://github.com/levinium/D2RExtractor/releases/tag/D2RExtractor_v1.1.7",
          "body": "- **Added incremental updates.** After a D2R patch...",
          "assets": [
            {
              "name": "D2RExtractor-Compiled-Standalone_v1.1.7.zip",
              "size": 66263285,
              "digest": "sha256:6d2a722b863f1569a12f94c0210c40a8ce8809005c7845054f5656fc147d3a68",
              "browser_download_url": "https://github.com/levinium/D2RExtractor/releases/download/D2RExtractor_v1.1.7/D2RExtractor-Compiled-Standalone_v1.1.7.zip"
            }
          ]
        }
        """;

    [Fact]
    public void TheFieldsThatMatterAreReadOutOfARealRelease()
    {
        var release = UpdateService.ParseRelease(RealShape);

        release.ShouldNotBeNull();
        release!.Tag.ShouldBe("D2RExtractor_v1.1.7");
        release.IsDraft.ShouldBeFalse();
        release.IsPreRelease.ShouldBeFalse();
        release.Notes.ShouldNotBeNullOrEmpty();

        release.Asset.ShouldNotBeNull();
        release.Asset!.Size.ShouldBe(66_263_285);
        release.Asset.Digest.ShouldStartWith("sha256:");
        release.Asset.Url.ShouldContain("releases/download");
        release.Asset.Name.ShouldBe("D2RExtractor-Compiled-Standalone_v1.1.7.zip");
    }

    /// <summary>
    /// The asset is matched on its extension, not its exact name. Releases up to 1.1.2 attached
    /// "D2RExtractor-Compiled-Standalone.zip" and everything since carries the version in the file
    /// name; pinning either spelling would have stopped the updater at the release that changed it.
    /// </summary>
    [Theory]
    [InlineData("D2RExtractor-Compiled-Standalone.zip")]
    [InlineData("D2RExtractor-Compiled-Standalone_v1.1.7.zip")]
    [InlineData("something-entirely-different.zip")]
    public void AnyZipAssetIsAccepted(string assetName)
    {
        string json = $$"""
            {
              "tag_name": "D2RExtractor_v1.1.7",
              "assets": [
                { "name": "{{assetName}}", "size": 1, "browser_download_url": "https://example/a.zip" }
              ]
            }
            """;

        UpdateService.ParseRelease(json)!.Asset!.Name.ShouldBe(assetName);
    }

    /// <summary>The checksum file published beside the zip is not the thing to download.</summary>
    [Fact]
    public void ANonZipAssetIsNotMistakenForTheDownload()
    {
        const string json = """
            {
              "tag_name": "D2RExtractor_v1.1.8",
              "assets": [
                { "name": "D2RExtractor.zip.sha256", "size": 90, "browser_download_url": "https://example/a.sha256" },
                { "name": "D2RExtractor.zip", "size": 100, "browser_download_url": "https://example/a.zip" }
              ]
            }
            """;

        UpdateService.ParseRelease(json)!.Asset!.Name.ShouldBe("D2RExtractor.zip");
    }

    [Fact]
    public void AReleaseWithNoAssetsStillReadsAsARelease()
    {
        const string json = """{ "tag_name": "D2RExtractor_v1.1.9", "assets": [] }""";

        var release = UpdateService.ParseRelease(json);

        release.ShouldNotBeNull();
        release!.Asset.ShouldBeNull();
    }

    /// <summary>
    /// An older asset predates GitHub's digest field. Missing is not a failure — the installer
    /// falls back to a length check and says so.
    /// </summary>
    [Fact]
    public void AnAssetWithNoDigestIsStillUsable()
    {
        const string json = """
            {
              "tag_name": "D2RExtractor_v1.1.4",
              "assets": [
                { "name": "a.zip", "size": 123, "browser_download_url": "https://example/a.zip" }
              ]
            }
            """;

        var asset = UpdateService.ParseRelease(json)!.Asset;

        asset.ShouldNotBeNull();
        asset!.Digest.ShouldBeNull();
        asset.Size.ShouldBe(123);
    }

    /// <summary>
    /// A captive portal or a proxy answering with an HTML login page is the usual cause, and it
    /// means exactly what a failed request means.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><body>Sign in</body></html>")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"no_tag\": true }")]
    public void AnythingThatIsNotAReleaseDocumentReadsAsNothing(string? body)
    {
        UpdateService.ParseRelease(body).ShouldBeNull();
    }

    [Fact]
    public void ADraftOrPreReleaseFlagIsCarriedThrough()
    {
        const string json = """
            { "tag_name": "D2RExtractor_v9.9.9", "draft": true, "prerelease": true, "assets": [] }
            """;

        var release = UpdateService.ParseRelease(json);

        release!.IsDraft.ShouldBeTrue();
        release.IsPreRelease.ShouldBeTrue();

        // And the decision refuses it, which is the point of reading the flags at all.
        UpdateDecision.For("1.1.8", release).Outcome.ShouldBe(UpdateOutcome.UpToDate);
    }
}
