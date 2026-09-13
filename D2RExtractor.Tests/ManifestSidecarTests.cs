using System.IO;
using System.Text;
using D2RExtractor.Models;
using D2RExtractor.Services;
using Shouldly;
using Xunit;

namespace D2RExtractor.Tests;

/// <summary>
/// The files a destination keeps about itself, exercised as real files.
///
/// <para>
/// These are what Undo reads to decide which files it owns, so a parsing mistake here is the
/// difference between removing an extraction and stranding 45 GB of it. They are small, plain and
/// written by hand rather than by a serializer, which is exactly the kind of code that looks
/// obviously right and is worth pinning anyway.
/// </para>
/// </summary>
public sealed class ManifestSidecarTests : IDisposable
{
    private readonly string _root;
    private readonly ExtractionTarget _target;

    public ManifestSidecarTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"d2rx-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _target = new ExtractionTarget { FolderPath = _root, Label = "Test" };
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* a leftover temp folder is not worth failing a test over */ }
    }

    private static ExtractionManifest NewManifest() => new()
    {
        ManifestVersion = ExtractionManifest.CurrentVersion,
        KeySource = "casc-ckey",
        EntryFile = ExtractionManifest.DefaultEntryFile,
    };

    // -----------------------------------------------------------------------
    // The manifest header
    // -----------------------------------------------------------------------

    [Fact]
    public void AManifestSurvivesBeingSavedAndReadBack()
    {
        var saved = NewManifest();
        saved.IsComplete = false;
        saved.EntryCount = 150_886;
        saved.TotalBytesExtracted = 44_748_504_128;
        saved.InternationalLanguage = "deDE";
        saved.InternationalExtracted = true;

        ManifestService.SaveManifest(_target, saved);
        var read = ManifestService.LoadManifest(_target);

        read.ShouldNotBeNull();
        read!.IsComplete.ShouldBeFalse();
        read.EntryCount.ShouldBe(150_886);
        read.TotalBytesExtracted.ShouldBe(44_748_504_128);
        read.InternationalLanguage.ShouldBe("deDE");
        read.InternationalExtracted.ShouldBe(true);
        read.IsLegacySchema.ShouldBeFalse();
    }

    [Fact]
    public void ADestinationWithNoManifestReadsAsNothingRatherThanThrowing()
    {
        ManifestService.LoadManifest(_target).ShouldBeNull();
    }

    /// <summary>
    /// A manifest nobody can parse must read as absent, not crash the app on startup. The
    /// installation then reports as not extracted, which is wrong but recoverable; an exception out
    /// of the load path is neither.
    /// </summary>
    [Fact]
    public void ACorruptManifestReadsAsAbsent()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_target.ManifestPath)!);
        File.WriteAllText(_target.ManifestPath, "{ this is not json");

        ManifestService.LoadManifest(_target).ShouldBeNull();
    }

    // -----------------------------------------------------------------------
    // The file list
    // -----------------------------------------------------------------------

    [Fact]
    public void EntriesAppendedDuringAnExtractionReadBackIntact()
    {
        var manifest = NewManifest();
        ManifestService.ResetEntries(_target, manifest);

        using (var writer = ManifestService.OpenEntryWriter(_target, manifest))
        {
            writer.Append(new ManifestEntry(@"data\global\excel\states.txt", "abc123", 26_643));
            writer.Append(new ManifestEntry(@"data\hd\items\misc\rune.dds", "def456", 48_212));
            writer.Append(new ManifestEntry(@"data\local\lng\strings\x.json", null, 94_258));
        }

        var entries = ManifestService.EnumerateEntries(_target, manifest).ToList();

        entries.Count.ShouldBe(3);
        entries[0].RelPath.ShouldBe(@"data\global\excel\states.txt");
        entries[0].Key.ShouldBe("abc123");
        entries[0].Size.ShouldBe(26_643);

        // A file whose source could not supply a key records an empty one, and must read back as
        // null rather than as the empty string — the update path compares against null.
        entries[2].Key.ShouldBeNull();
    }

    /// <summary>
    /// The worst a crash mid-append can leave: a final line cut off partway. Every complete record
    /// before it still has to be readable, because those name files that are on disk and that Undo
    /// is responsible for.
    /// </summary>
    [Fact]
    public void ATornFinalLineIsSkippedAndEverythingBeforeItSurvives()
    {
        var manifest = NewManifest();
        string path = ManifestService.GetEntryFilePath(_target, manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        File.WriteAllText(path,
            "data\\a.txt\tkey1\t100\n" +
            "data\\b.txt\tkey2\t200\n" +
            "data\\c.txt\tkey3",                 // cut off before the size
            new UTF8Encoding(false));

        var entries = ManifestService.EnumerateEntries(_target, manifest).ToList();

        entries.Count.ShouldBe(2);
        entries.Select(e => e.RelPath).ShouldBe(new[] { @"data\a.txt", @"data\b.txt" });
    }

    [Fact]
    public void WritingTheWholeListReplacesWhatWasThereAndCountsIt()
    {
        var manifest = NewManifest();

        using (var writer = ManifestService.OpenEntryWriter(_target, manifest))
            writer.Append(new ManifestEntry(@"data\old.txt", "k", 1));

        ManifestService.WriteAllEntries(_target, manifest, new[]
        {
            new ManifestEntry(@"data\new1.txt", "k1", 10),
            new ManifestEntry(@"data\new2.txt", "k2", 20),
        });

        var entries = ManifestService.EnumerateEntries(_target, manifest).ToList();

        entries.Count.ShouldBe(2);
        entries.ShouldNotContain(e => e.RelPath == @"data\old.txt");
        manifest.EntryCount.ShouldBe(2);
    }

    [Fact]
    public void AFreshExtractionDoesNotInheritThePreviousRunsFileList()
    {
        var manifest = NewManifest();

        using (var writer = ManifestService.OpenEntryWriter(_target, manifest))
            writer.Append(new ManifestEntry(@"data\stale.txt", "k", 1));

        ManifestService.ResetEntries(_target, manifest);

        ManifestService.EnumerateEntries(_target, manifest).ShouldBeEmpty();
        manifest.EntryCount.ShouldBe(0);
    }

    // -----------------------------------------------------------------------
    // What the last run changed
    // -----------------------------------------------------------------------

    [Fact]
    public void AllThreeKindsOfChangeSurviveARoundTrip()
    {
        ManifestService.WriteChanges(_target, new[]
        {
            new ExtractionChange(ChangeKind.Added, @"data\hd\new.dds", 48_212),
            new ExtractionChange(ChangeKind.Updated, @"data\global\excel\states.txt", 26_643),
            new ExtractionChange(ChangeKind.Removed, @"data\hd\gone.webm", 318_204_112),
        });

        var changes = ManifestService.EnumerateChanges(_target).ToList();

        changes.Count.ShouldBe(3);
        changes[0].Kind.ShouldBe(ChangeKind.Added);
        changes[1].Kind.ShouldBe(ChangeKind.Updated);
        changes[2].Kind.ShouldBe(ChangeKind.Removed);
        changes[2].RelPath.ShouldBe(@"data\hd\gone.webm");
        changes[2].Size.ShouldBe(318_204_112);
    }

    [Fact]
    public void ADestinationWithNoHistoryYieldsNothing()
    {
        ManifestService.EnumerateChanges(_target).ShouldBeEmpty();
        ManifestService.LoadRunRecord(_target).ShouldBeNull();
    }

    [Fact]
    public void ARunRecordSurvivesBeingSavedAndReadBack()
    {
        ManifestService.SaveRunRecord(_target, new ExtractionRunRecord
        {
            Kind = RunKind.Update,
            FilesAdded = 84_856,
            FilesUpdated = 3,
            FilesRemoved = 1,
            FilesUnchanged = 66_030,
            BytesWritten = 34_570_613_602,
            HasChangeList = true,
        });

        var read = ManifestService.LoadRunRecord(_target);

        read.ShouldNotBeNull();
        read!.Kind.ShouldBe(RunKind.Update);
        read.FilesAdded.ShouldBe(84_856);
        read.BytesWritten.ShouldBe(34_570_613_602);
        read.HasChangeList.ShouldBeTrue();
        read.TotalChanged.ShouldBe(84_856 + 3 + 1);
    }

    // -----------------------------------------------------------------------
    // Bookkeeping vs content
    // -----------------------------------------------------------------------

    /// <summary>
    /// Every file the app keeps about itself lives inside the folder it extracts into, so the scan
    /// that walks that folder has to know which ones are its own. Miss one and the next update sees
    /// a file the archives do not contain.
    /// </summary>
    [Fact]
    public void EveryBookkeepingFileIsNamedAsSuchAndNoneAreMissed()
    {
        var manifest = NewManifest();

        var bookkeeping = ManifestService.BookkeepingPaths(_target, manifest).ToList();
        var names = bookkeeping.Select(Path.GetFileName).ToList();

        names.ShouldContain(".extraction_manifest.json");
        names.ShouldContain(ExtractionManifest.DefaultEntryFile);
        names.ShouldContain(ExtractionRunRecord.DefaultChangeFile);
        names.ShouldContain(".extraction_run.json");

        // All of them inside the destination, so the scan's comparison against full paths matches.
        bookkeeping.ShouldAllBe(p => p.StartsWith(_root, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UndoRemovesEveryTraceOfTheExtractionsRecords()
    {
        var manifest = NewManifest();
        ManifestService.SaveManifest(_target, manifest);

        using (var writer = ManifestService.OpenEntryWriter(_target, manifest))
            writer.Append(new ManifestEntry(@"data\a.txt", "k", 1));

        ManifestService.WriteChanges(_target, new[] { new ExtractionChange(ChangeKind.Added, @"data\a.txt", 1) });
        ManifestService.SaveRunRecord(_target, new ExtractionRunRecord { Kind = RunKind.Extract });

        ManifestService.DeleteManifest(_target);
        ManifestService.DeleteRunRecord(_target);

        foreach (string path in ManifestService.BookkeepingPaths(_target, manifest))
            File.Exists(path).ShouldBeFalse($"{Path.GetFileName(path)} was left behind");
    }
}
