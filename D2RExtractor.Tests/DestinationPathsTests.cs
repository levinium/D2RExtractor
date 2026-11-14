using D2RExtractor.Models;
using Shouldly;
using Xunit;

namespace D2RExtractor.Tests;

/// <summary>
/// Where two destinations are allowed to point.
///
/// <para>
/// This decides whether the app will let someone create a pair of destinations that delete each
/// other. Each destination's update treats any file under its tree that the archives do not contain
/// as a leftover from an old patch and removes it, so a nested pair quietly eats itself — and the
/// person who set it up would see files disappearing with no idea why.
/// </para>
/// </summary>
public class DestinationPathsTests
{
    /// <summary>
    /// Test paths are written Windows-shaped because that is what the app's users mostly type.
    /// On a platform without drive letters they are mapped onto an equivalent rooted path, so
    /// the same cases exercise the same rules rather than silently becoming relative paths
    /// resolved against the test runner's working directory.
    /// </summary>
    private static string Rooted(string windowsShaped) =>
        OperatingSystem.IsWindows()
            ? windowsShaped
            : "/" + windowsShaped[3..].Replace('\\', '/');

    /// <summary>
    /// The trap this whole class exists for. "C:\D2RMods" starts with "C:\D2R" as a string and is
    /// nothing to do with it as a folder, so a comparison that forgets the separator refuses a
    /// perfectly good destination and leaves the person with no way to add it.
    /// </summary>
    [Theory]
    [InlineData(@"C:\D2RMods", @"C:\D2R")]
    [InlineData(@"C:\D2R", @"C:\D2RMods")]
    [InlineData(@"C:\Games\D2R-backup", @"C:\Games\D2R")]
    public void AFolderThatMerelySharesAPrefixIsNotInsideTheOther(string path, string other)
    {
        DestinationPaths.IsUnder(path, other).ShouldBeFalse();
        DestinationPaths.Overlap(path, other).ShouldBeFalse();
    }

    [Theory]
    [InlineData(@"C:\D2R\mods", @"C:\D2R")]
    [InlineData(@"C:\D2R\a\b\c", @"C:\D2R")]
    [InlineData(@"C:\D2R\data", @"C:\D2R")]
    public void AFolderInsideAnotherIsCaught(string child, string parent)
    {
        DestinationPaths.IsUnder(Rooted(child), Rooted(parent)).ShouldBeTrue();

        // Both directions, because the caller does not know which of the two it is adding.
        DestinationPaths.Overlap(Rooted(child), Rooted(parent)).ShouldBeTrue();
        DestinationPaths.Overlap(Rooted(parent), Rooted(child)).ShouldBeTrue();
    }

    [Fact]
    public void AFolderIsNotInsideItself()
    {
        DestinationPaths.IsUnder(@"C:\D2R", @"C:\D2R").ShouldBeFalse();
    }

    [Theory]
    [InlineData(@"C:\D2R\", @"C:\D2R")]
    [InlineData(@"C:\D2R\mods\..", @"C:\D2R")]
    [InlineData(@"C:\D2R\.\", @"C:\D2R")]
    public void TheSameFolderSpelledDifferentlyIsStillTheSameFolder(string a, string b)
    {
        DestinationPaths.AreSame(Rooted(a), Rooted(b)).ShouldBeTrue();

        // Adding a folder that is already a destination has to be refused too, not just nesting.
        DestinationPaths.Overlap(Rooted(a), Rooted(b)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(@"C:\D2R", @"D:\D2R")]
    [InlineData(@"C:\Games\A", @"C:\Games\B")]
    public void UnrelatedFoldersDoNotOverlap(string a, string b)
    {
        DestinationPaths.Overlap(a, b).ShouldBeFalse();
    }

    /// <summary>
    /// Junk has to resolve to "no match", never to a match. A comparison that treated two
    /// unreadable paths as equal would refuse to add a destination and blame an existing one.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnusablePathIsNotAPath(string? junk)
    {
        DestinationPaths.Normalize(junk).ShouldBeNull();

        DestinationPaths.AreSame(junk, @"C:\D2R").ShouldBeFalse();
        DestinationPaths.AreSame(@"C:\D2R", junk).ShouldBeFalse();
        DestinationPaths.Overlap(junk, @"C:\D2R").ShouldBeFalse();
        DestinationPaths.Overlap(@"C:\D2R", junk).ShouldBeFalse();
    }

    [Fact]
    public void TwoUnusablePathsAreNotEqualToEachOther()
    {
        DestinationPaths.AreSame(null, null).ShouldBeFalse();
        DestinationPaths.AreSame("", "").ShouldBeFalse();
    }

    [Fact]
    public void NormalizeDropsTheTrailingSeparator()
    {
        DestinationPaths.Normalize(Rooted(@"C:\D2R\")).ShouldBe(Rooted(@"C:\D2R"));
        DestinationPaths.Normalize(Rooted(@"C:\D2R")).ShouldBe(Rooted(@"C:\D2R"));
    }

    [Fact]
    public void WhetherCaseAloneMakesADifferentFolderFollowsThePlatform()
    {
        // Not a quirk worth hiding behind a skip: it is the rule itself. Windows and macOS
        // resolve paths case-insensitively, Linux does not, so one spelling names one folder
        // on one platform and two on another - and the overlap check has to agree with the
        // filesystem it is actually running on.
        bool sameFolder = !OperatingSystem.IsLinux();

        DestinationPaths.AreSame(Rooted(@"c:\d2r"), Rooted(@"C:\D2R")).ShouldBe(sameFolder);
        DestinationPaths.Overlap(Rooted(@"c:\d2r"), Rooted(@"C:\D2R")).ShouldBe(sameFolder);
    }
}
