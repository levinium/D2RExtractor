using D2RExtractor.Services;
using Shouldly;
using Xunit;

namespace D2RExtractor.Tests;

/// <summary>
/// CASC paths are backslash-canonical and case-insensitive. On Windows that is simply what paths
/// already are, so these rules are a no-op; everywhere else they are the difference between an
/// extracted tree and a directory full of files with backslashes in their names.
/// </summary>
public class PlatformPathsTests
{
    private static bool OnWindows => OperatingSystem.IsWindows();

    [Fact]
    public void SeparatorsSurviveARoundTrip()
    {
        // Manifests keep storing '\' whatever the platform, so the on-disk format does not
        // change: the translation happens at the filesystem boundary and nowhere else.
        const string casc = @"data\global\allcofs.bin";

        PlatformPaths.ToCasc(PlatformPaths.ToNative(casc)).ShouldBe(casc);
    }

    [Fact]
    public void ToNativeUsesTheHostSeparator()
    {
        PlatformPaths.ToNative(@"data\global\x.dc6")
            .ShouldBe(OnWindows ? @"data\global\x.dc6" : "data/global/x.dc6");
    }

    [Fact]
    public void ToCascIsHowDiskPathsBecomeManifestKeys()
    {
        // The reverse boundary. Path.GetRelativePath returns native separators, and those keys
        // have to match the backslash keys already recorded in the manifest - if they do not,
        // every file looks missing and the whole extraction is rewritten on every update.
        string native = OnWindows ? @"data\hd\x.texture" : "data/hd/x.texture";

        PlatformPaths.ToCasc(native).ShouldBe(@"data\hd\x.texture");
    }

    [Fact]
    public void CombineProducesRealNestedDirectories()
    {
        string root = NewTempDir();

        string combined = PlatformPaths.Combine(root, @"data\global\x.bin");

        Path.GetFileName(combined).ShouldBe("x.bin");
        Path.GetFileName(Path.GetDirectoryName(combined)!).ShouldBe("global");
        combined.ShouldNotContain(OnWindows ? "/" : @"\");
    }

    [Fact]
    public void AnExistingDirectoryKeepsItsOwnCasing()
    {
        // The game ships "Data" while CASC paths say "data". Extracting into the game folder has
        // to land in the directory that is already there rather than beside it.
        string root = NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "Data"));

        string resolved = PlatformPaths.ResolveDirectory(root, "data");

        Path.GetFileName(resolved).ShouldBe(OnWindows ? "data" : "Data");
        Directory.Exists(resolved).ShouldBeTrue();
    }

    [Fact]
    public void CombineLandsInsideTheDirectoryThatAlreadyExists()
    {
        string root = NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "Data"));

        string combined = PlatformPaths.Combine(root, @"data\global\x.bin");

        combined.ShouldStartWith(Path.Combine(root, OnWindows ? "data" : "Data"));
    }

    [Fact]
    public void AMissingDirectoryIsLeftAsAsked()
    {
        // A destination pointed somewhere fresh has nothing to match, so the name is used as
        // written and the create-then-use flow still works.
        string root = NewTempDir();

        Path.GetFileName(PlatformPaths.ResolveDirectory(root, "data")).ShouldBe("data");
    }

    [Fact]
    public void ResolutionDoesNotChangeItsMindMidOperation()
    {
        // The stability that matters: an extraction creates directories as it runs, so a probe
        // that re-checked the disk could answer "Data" before the first write and "data" after,
        // scattering files and manifests across both.
        string root = NewTempDir();
        string before = PlatformPaths.ResolveDirectory(root, "data");

        Directory.CreateDirectory(Path.Combine(root, "Data"));

        PlatformPaths.ResolveDirectory(root, "data").ShouldBe(before);
    }

    private static string NewTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "d2rx-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
