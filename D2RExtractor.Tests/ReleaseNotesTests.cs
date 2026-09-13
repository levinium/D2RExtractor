using D2RExtractor.Services.Updates;
using Shouldly;
using Xunit;

namespace D2RExtractor.Tests;

/// <summary>
/// Turning a release body into something worth reading in a plain TextBlock.
///
/// <para>
/// Deliberately not a Markdown parser. It removes the handful of markers these notes actually use
/// and leaves everything else as written, so the tests that matter are as much about what it does
/// NOT touch as what it does.
/// </para>
/// </summary>
public class ReleaseNotesTests
{
    [Fact]
    public void BoldMarkersAreRemovedWithoutLeavingStrays()
    {
        ReleaseNotes.ToPlainText("- **Added incremental updates.** After a patch...")
            .ShouldBe("• Added incremental updates. After a patch...");
    }

    [Theory]
    [InlineData("a **b** c", "a b c")]
    [InlineData("a __b__ c", "a b c")]
    [InlineData("use `data\\file.txt` here", "use data\\file.txt here")]
    public void EmphasisAndCodeMarkersGo(string input, string expected)
    {
        ReleaseNotes.ToPlainText(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("- one", "• one")]
    [InlineData("* two", "• two")]
    [InlineData("> quoted", "quoted")]
    [InlineData("## Heading", "Heading")]
    [InlineData("### Deeper", "Deeper")]
    public void TheMarkersTheseNotesActuallyUseAreHandled(string input, string expected)
    {
        ReleaseNotes.ToPlainText(input).ShouldBe(expected);
    }

    /// <summary>
    /// The marker is matched with its trailing space, so a line that merely starts with a dash — a
    /// negative number, or an em-dash aside — is left alone.
    /// </summary>
    [Theory]
    [InlineData("-5 degrees")]
    [InlineData("--force is required")]
    [InlineData("*emphasis at the start* of a line")]
    public void ALineThatMerelyStartsWithAMarkerCharacterIsNotABullet(string input)
    {
        ReleaseNotes.ToPlainText(input).ShouldNotStartWith("•");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingInGivesNothingOut(string? input)
    {
        ReleaseNotes.ToPlainText(input).ShouldBe(string.Empty);
    }

    [Fact]
    public void WindowsAndUnixLineEndingsBothSurvive()
    {
        ReleaseNotes.ToPlainText("- one\r\n- two").ShouldBe("• one\r\n• two".Replace("\r\n", Environment.NewLine));
    }

    /// <summary>
    /// Indentation is what makes a sub-point read as one, so it survives inside the body. The block
    /// as a whole is still trimmed, which is why this has to be checked in the middle of a document
    /// rather than on a line of its own.
    /// </summary>
    [Fact]
    public void NestedBulletsKeepTheirIndent()
    {
        string result = ReleaseNotes.ToPlainText("- top\n  - indented\n- back");

        result.ShouldContain("  • indented");
        result.Split('\n').Select(l => l.TrimEnd()).ShouldBe(new[] { "• top", "  • indented", "• back" });
    }
}
