using D2RExtractor.Models;
using Shouldly;
using Xunit;

namespace D2RExtractor.Tests;

/// <summary>
/// How several destinations roll up into the one status and set of buttons a row shows.
///
/// <para>
/// The rule that matters is that "Extracted" requires ALL of them. A row reading "Extracted" while
/// one of its two destinations is empty is lying in the direction that costs someone a mods folder
/// they believed was populated — and they would only find out by looking in it.
/// </para>
/// </summary>
public class InstallationStateTests
{
    private const string Game = @"D:\Games\Diablo II Resurrected";

    private static ExtractionTarget Target(string path, bool extracted, bool enabled = true)
    {
        var t = new ExtractionTarget { FolderPath = path, Enabled = enabled };

        // null = no manifest, true = complete. International is off, so it plays no part.
        t.RefreshState(extracted ? true : null, null, internationalEnabled: false, null, null);
        return t;
    }

    private static D2RInstallation Install(params ExtractionTarget[] targets)
    {
        var install = new D2RInstallation { Name = "Test", FolderPath = Game };
        install.Targets = targets.ToList();
        install.RefreshAggregateState();
        return install;
    }

    // -----------------------------------------------------------------------
    // The default, which is what almost everyone has
    // -----------------------------------------------------------------------

    /// <summary>
    /// A settings file written before 1.1.8 has no Targets at all. It has to behave exactly as it
    /// always did, with the game folder as the one destination and no migration step.
    /// </summary>
    [Fact]
    public void AnInstallationWithNoConfiguredDestinationsGetsTheGameFolder()
    {
        var install = new D2RInstallation { Name = "Legacy", FolderPath = Game };

        install.Targets.ShouldBeEmpty();
        install.EffectiveTargets.Count.ShouldBe(1);
        install.EffectiveTargets[0].FolderPath.ShouldBe(Game);
        install.EffectiveTargets[0].IsGameFolder(Game).ShouldBeTrue();
        install.HasCustomTargets.ShouldBeFalse();
        install.SkipsGameFolder.ShouldBeFalse();
    }

    /// <summary>The implicit destination follows the folder, which can be edited after the fact.</summary>
    [Fact]
    public void TheImplicitDestinationFollowsTheInstallationFolder()
    {
        var install = new D2RInstallation { Name = "Legacy", FolderPath = Game };
        install.EffectiveTargets[0].FolderPath.ShouldBe(Game);

        install.FolderPath = @"E:\Elsewhere\D2R";

        install.EffectiveTargets[0].FolderPath.ShouldBe(@"E:\Elsewhere\D2R");
    }

    [Fact]
    public void OneExtractedDestinationReadsExactlyAsItAlwaysDid()
    {
        var install = Install(Target(Game, extracted: true));

        install.IsExtracted.ShouldBeTrue();
        install.StatusText.ShouldBe("Extracted");
        install.PrimaryActionLabel.ShouldBe("Update");
        install.CanUndo.ShouldBeTrue();
    }

    // -----------------------------------------------------------------------
    // More than one
    // -----------------------------------------------------------------------

    [Fact]
    public void EveryDestinationHasToBeExtractedBeforeTheRowSaysSo()
    {
        var install = Install(
            Target(Game, extracted: true),
            Target(@"C:\Mods", extracted: false));

        install.IsExtracted.ShouldBeFalse();
        install.StatusText.ShouldBe("Partial (1 of 2)");
    }

    [Fact]
    public void AllExtractedCountsThemInTheStatus()
    {
        var install = Install(
            Target(Game, extracted: true),
            Target(@"C:\Mods", extracted: true));

        install.IsExtracted.ShouldBeTrue();
        install.StatusText.ShouldBe("Extracted (2 of 2)");
    }

    /// <summary>
    /// With one destination extracted and one empty, neither IsExtracted nor IsPartiallyExtracted
    /// is true. Reading Undo off those would grey it out while 45 GB sat on disk with no other way
    /// to remove it, which is why it is asked of the destinations instead.
    /// </summary>
    [Fact]
    public void UndoStaysAvailableWhileAnyDestinationHoldsFiles()
    {
        var install = Install(
            Target(Game, extracted: true),
            Target(@"C:\Mods", extracted: false));

        install.AnyTargetExtracted.ShouldBeTrue();
        install.CanUndo.ShouldBeTrue();
        install.CanUpdate.ShouldBeTrue();

        // And the button offers to update rather than to start from nothing.
        install.PrimaryActionLabel.ShouldBe("Update");
    }

    [Fact]
    public void NothingExtractedAnywhereOffersAFullExtraction()
    {
        var install = Install(
            Target(Game, extracted: false),
            Target(@"C:\Mods", extracted: false));

        install.AnyTargetExtracted.ShouldBeFalse();
        install.CanExtract.ShouldBeTrue();
        install.CanUndo.ShouldBeFalse();
        install.PrimaryActionLabel.ShouldBe("Extract");
        install.StatusText.ShouldBe("Ready");
    }

    // -----------------------------------------------------------------------
    // Turning one off
    // -----------------------------------------------------------------------

    /// <summary>
    /// A disabled destination is left exactly as it is rather than cleaned up: turning one off is
    /// not the same as asking for its 45 GB to be deleted.
    /// </summary>
    [Fact]
    public void ADisabledDestinationTakesNoPartInAnything()
    {
        var install = Install(
            Target(Game, extracted: true),
            Target(@"C:\Mods", extracted: false, enabled: false));

        install.ActiveTargets.Count.ShouldBe(1);
        install.IsExtracted.ShouldBeTrue();
        install.StatusText.ShouldBe("Extracted");
    }

    [Fact]
    public void TurningEveryDestinationOffLeavesNothingToDo()
    {
        var install = Install(
            Target(Game, extracted: true, enabled: false));

        install.ActiveTargets.ShouldBeEmpty();
        install.CanExtract.ShouldBeFalse();
        install.CanUndo.ShouldBeFalse();
        install.StatusText.ShouldBe("No destinations");
    }

    // -----------------------------------------------------------------------
    // The warning that stops a silent disappointment
    // -----------------------------------------------------------------------

    /// <summary>
    /// D2R only loads extracted files from its own folder. Extracting solely to a mods folder is a
    /// legitimate thing to want and the app allows it — but it has to know, so it can say so.
    /// </summary>
    [Fact]
    public void AnInstallationWritingNowhereNearTheGameFolderKnowsIt()
    {
        var install = Install(Target(@"C:\Mods", extracted: false));

        install.SkipsGameFolder.ShouldBeTrue();
        install.HasCustomTargets.ShouldBeTrue();
    }

    [Fact]
    public void DisablingTheGameFolderCountsAsSkippingIt()
    {
        var install = Install(
            Target(Game, extracted: true, enabled: false),
            Target(@"C:\Mods", extracted: true));

        install.SkipsGameFolder.ShouldBeTrue();
    }

    [Fact]
    public void TheGameFolderIsRecognisedWhateverItsSpelling()
    {
        var install = Install(Target(Game + @"\", extracted: true));

        install.SkipsGameFolder.ShouldBeFalse();
    }

    // -----------------------------------------------------------------------
    // An interrupted run
    // -----------------------------------------------------------------------

    [Fact]
    public void AnInterruptedDestinationOffersToResume()
    {
        var interrupted = new ExtractionTarget { FolderPath = Game };
        interrupted.RefreshState(false, null, internationalEnabled: false, null, null);

        var install = Install(interrupted);

        install.IsInterrupted.ShouldBeTrue();
        install.PrimaryActionLabel.ShouldBe("Resume");
        install.CanUndo.ShouldBeTrue();
    }

    /// <summary>
    /// An interrupted destination is the more urgent of the two states and is what the button
    /// should offer; a pending language change is only reported once nothing is half-written.
    /// </summary>
    [Fact]
    public void ResumeWinsOverAPendingLanguageChange()
    {
        var interrupted = new ExtractionTarget { FolderPath = Game };
        interrupted.RefreshState(false, null, internationalEnabled: false, null, null);

        var languagePending = new ExtractionTarget { FolderPath = @"C:\Mods" };
        languagePending.RefreshState(true, false, internationalEnabled: true, null, "deDE");

        var install = Install(interrupted, languagePending);

        install.IsInterrupted.ShouldBeTrue();
        install.IsInternationalPending.ShouldBeFalse();
        install.PrimaryActionLabel.ShouldBe("Resume");
    }
}
