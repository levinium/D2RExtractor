using D2RExtractor.Models;
using D2RExtractor.Services;

namespace D2RExtractor.Cli;

/// <summary>
/// The CLI verbs. State lives in the same settings.json the GUI uses, via ManifestService, so an
/// installation added in one appears in the other.
///
/// <para>
/// Every operation runs over an installation's active destinations rather than a single folder,
/// which is what the GUI does too. A plain install has exactly one - the game folder - so the
/// common case reads as if destinations did not exist.
/// </para>
/// </summary>
internal static class Commands
{
    // ------------------------------------------------------------ installations

    public static int Add(CommandLine cli)
    {
        if (cli.Target == null) return Fail("add needs a path: d2rextractor add <folder>");

        string path = ResolveInputPath(cli.Target);

        if (!Directory.Exists(path))
        {
            string hint = path != cli.Target ? $"\n  (read '{cli.Target}' as '{path}')" : "";
            return Fail($"No such folder: {path}{hint}");
        }

        if (CascExtractorService.ValidateInstallationFolder(path) is { } problem) return Fail(problem);

        var installs = ManifestService.LoadInstallations();
        if (installs.Any(i => PathsEqual(i.FolderPath, path)))
        {
            Console.WriteLine($"Already added: {path}");
            return 0;
        }

        installs.Add(new D2RInstallation
        {
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n
                ? n : "Diablo II Resurrected",
            FolderPath = path,
        });
        ManifestService.SaveInstallations(installs);
        Console.WriteLine($"Added: {path}");
        return 0;
    }

    public static int List()
    {
        var installs = ManifestService.LoadInstallations();
        if (installs.Count == 0)
        {
            Console.WriteLine("No installations. Add one with: d2rextractor add <folder>");
            return 0;
        }

        for (int i = 0; i < installs.Count; i++)
        {
            var install = installs[i];
            Refresh(install);
            Console.WriteLine($"[{i + 1}] {install.Name}  ({StateOf(install)})");
            Console.WriteLine($"    {install.FolderPath}");

            // Only worth listing when the user has set some up; otherwise it is just the game folder.
            if (install.HasCustomTargets)
                foreach (var t in install.ActiveTargets)
                    Console.WriteLine($"      -> {t.DisplayName}  ({(t.IsExtracted ? "extracted" : t.IsPartiallyExtracted ? "incomplete" : "not extracted")})");
        }
        return 0;
    }

    public static int Forget(CommandLine cli)
    {
        var installs = ManifestService.LoadInstallations();
        if (!TryResolve(cli.Target, installs, out var install, out int rc)) return rc;

        installs.RemoveAll(i => PathsEqual(i.FolderPath, install.FolderPath));
        ManifestService.SaveInstallations(installs);
        Console.WriteLine($"Forgot: {install.FolderPath}");
        Console.WriteLine("Extracted files were left alone. Use 'undo' first to remove them.");
        return 0;
    }

    // ------------------------------------------------------------ inspection

    public static int Status(CommandLine cli)
    {
        var installs = ManifestService.LoadInstallations();
        if (!TryResolve(cli.Target, installs, out var install, out int rc)) return rc;

        var prefs = Preferences(cli);
        Refresh(install);

        Console.WriteLine(install.Name);
        Console.WriteLine($"  folder        : {install.FolderPath}");
        Console.WriteLine($"  state         : {StateOf(install)}");

        foreach (var target in install.ActiveTargets)
        {
            var manifest = ManifestService.LoadManifest(target);
            var planned = CascExtractorService.PlanOperation(
                manifest, prefs.ExtractInternationalFiles, prefs.InternationalLanguage);

            Console.WriteLine($"  destination   : {target.DisplayName}");
            if (manifest != null)
            {
                Console.WriteLine($"    extracted at: {manifest.ExtractedAt:u}");
                Console.WriteLine($"    on disk     : {ConsoleProgress.Bytes(manifest.TotalBytesExtracted)}");
                Console.WriteLine($"    international: {(manifest.InternationalExtracted == true ? manifest.InternationalLanguage ?? "yes" : "no")}");
            }
            Console.WriteLine($"    next action : {planned.ToString().ToLowerInvariant()}");
        }

        foreach (string warning in DiskSpaceWarnings(install, prefs))
            Console.WriteLine($"  warning       : {warning}");

        return 0;
    }

    // ------------------------------------------------------------ operations

    public static int Extract(CommandLine cli, CancellationToken ct)
    {
        var installs = ManifestService.LoadInstallations();
        if (!TryResolve(cli.Target, installs, out var install, out int rc)) return rc;

        var prefs = Preferences(cli);

        foreach (string warning in DiskSpaceWarnings(install, prefs))
        {
            Console.WriteLine(warning);
            if (!Confirm(cli, "Continue anyway?")) return 1;
        }

        string where = Scope(install);
        if (!Confirm(cli, $"Extract D2R game files {where}? This writes roughly 45-70 GB."))
            return 1;

        var service = new CascExtractorService();
        foreach (var target in install.ActiveTargets)
        {
            if (install.HasCustomTargets) Console.WriteLine($"-> {target.DisplayName}");

            // A destination that already has a manifest is updated, never extracted afresh, as in
            // the GUI. Extract resets the manifest before writing, so a cancelled re-extraction
            // would leave it listing only the files written so far, and undo would strand the rest.
            var existing = ManifestService.LoadManifest(target);
            if (existing != null)
            {
                Console.WriteLine($"Already extracted at {target.DisplayName}; updating instead.");
                RunUpdate(service, install, target, existing, prefs, cli, ct);
                continue;
            }

            using var progress = new ConsoleProgress();
            var manifest = service.Extract(install, target, prefs.ExtractInternationalFiles,
                prefs.InternationalLanguage, progress, Log, ct);
            progress.Dispose();

            Console.WriteLine($"Extraction complete: {ConsoleProgress.Bytes(manifest.TotalBytesExtracted)} written.");
        }

        Console.WriteLine("Launch D2R with -direct -txt to use the extracted files.");
        return 0;
    }

    public static int Update(CommandLine cli, CancellationToken ct)
    {
        var installs = ManifestService.LoadInstallations();
        if (!TryResolve(cli.Target, installs, out var install, out int rc)) return rc;

        var prefs = Preferences(cli);
        var service = new CascExtractorService();
        bool any = false;

        foreach (var target in install.ActiveTargets)
        {
            var manifest = ManifestService.LoadManifest(target);
            if (manifest == null)
            {
                Console.WriteLine($"Nothing extracted yet at {target.DisplayName} — skipping.");
                continue;
            }

            any = true;
            if (install.HasCustomTargets) Console.WriteLine($"-> {target.DisplayName}");
            RunUpdate(service, install, target, manifest, prefs, cli, ct);
        }

        return any ? 0 : Fail("Nothing extracted yet for this installation. Run 'extract' first.");
    }

    private static void RunUpdate(CascExtractorService service, D2RInstallation install,
        ExtractionTarget target, ExtractionManifest manifest, AppPreferences prefs,
        CommandLine cli, CancellationToken ct)
    {
        using var progress = new ConsoleProgress();
        var summary = service.UpdateExtraction(install, target, manifest,
            prefs.ExtractInternationalFiles, prefs.InternationalLanguage,
            cli.Verify || prefs.VerifyFileContents, progress, Log, ct);
        progress.Dispose();

        Console.WriteLine($"Update complete: {summary.FilesWritten:N0} written " +
                          $"({ConsoleProgress.Bytes(summary.BytesWritten)}), " +
                          $"{summary.FilesRemoved:N0} removed, {summary.FilesUnchanged:N0} unchanged.");
    }

    /// <summary>
    /// Checks free space where the files will be written: each active destination, not the game
    /// folder. Destinations on one filesystem share its free space, so their requirements are
    /// summed per filesystem, as the GUI does per drive.
    /// </summary>
    private static IEnumerable<string> DiskSpaceWarnings(D2RInstallation install, AppPreferences prefs)
    {
        long perDestination = (prefs.ExtractInternationalFiles ? 50L : 48L) * 1024 * 1024 * 1024;

        return install.ActiveTargets
            .GroupBy(t => VolumeKey(t.FolderPath))
            .Select(g => CascExtractorService.CheckDiskSpace(g.First().FolderPath, perDestination * g.Count()))
            .OfType<string>();
    }

    /// <summary>
    /// Identifies the filesystem holding a path. On Unix, DriveInfo built from a path keeps that
    /// path as its name and exposes no device id, so two paths that report the same total and
    /// free bytes are treated as one filesystem.
    /// </summary>
    private static string VolumeKey(string path)
    {
        try
        {
            var volume = CascExtractorService.FindVolume(path);
            if (volume == null) return path;
            return OperatingSystem.IsWindows()
                ? volume.Name.ToUpperInvariant()
                : $"{volume.TotalSize}:{volume.AvailableFreeSpace}";
        }
        catch
        {
            return path;
        }
    }

    public static int Undo(CommandLine cli, CancellationToken ct)
    {
        var installs = ManifestService.LoadInstallations();
        if (!TryResolve(cli.Target, installs, out var install, out int rc)) return rc;

        var withManifest = install.ActiveTargets.Where(t => ManifestService.LoadManifest(t) != null).ToList();
        if (withManifest.Count == 0) return Fail("Nothing extracted yet for this installation.");

        if (!Confirm(cli, $"Remove all extracted files {Scope(install)}?")) return 1;

        var service = new CascExtractorService();
        foreach (var target in withManifest)
        {
            if (install.HasCustomTargets) Console.WriteLine($"-> {target.DisplayName}");

            using var progress = new ConsoleProgress();
            service.UndoExtraction(target, progress, Log, ct);
            progress.Dispose();
        }

        Console.WriteLine("Undo complete. The installation is back to its pre-extraction state.");
        return 0;
    }

    // ------------------------------------------------------------ help

    public static int Version()
    {
        var v = typeof(Commands).Assembly.GetName().Version;
        Console.WriteLine($"d2rextractor {v?.ToString(3) ?? "unknown"}");
        return 0;
    }

    public static int Help()
    {
        Console.WriteLine("""
            d2rextractor — extract Diablo II: Resurrected game files for faster loading

            USAGE
              d2rextractor <command> [target] [options]

            A target is either an installation number from 'list' or a folder path.
            When exactly one installation is configured, the target may be omitted.

            COMMANDS
              add <folder>        Register a D2R installation
              list                Show registered installations and their state
              forget [target]     Unregister an installation (leaves files on disk)
              status [target]     Show extraction state and what would happen next
              extract [target]    Extract game files (~45-70 GB)
              update [target]     Re-sync after a patch, writing only what changed
              undo [target]       Delete extracted files, restoring the install

            OPTIONS
              --international <lang>   Also extract this language (e.g. dede, frfr, eses)
              --no-international       Skip international files
              --verify                 Checksum file contents during update, not just sizes
              -y, --yes                Do not prompt for confirmation
              -h, --help               Show this help
                  --version            Show the version

            NOTES
              Settings are shared with the GUI, so installations and destinations
              configured in one appear in the other. Operations run over every enabled
              destination.

              Battle.net installs need the native CascLib (libcasc.so) on the library
              path. Steam installs use the built-in reader and need nothing extra.
            """);
        return 0;
    }

    // ------------------------------------------------------------ helpers

    private static void Log(string message) => Console.WriteLine($"  {message}");

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"d2rextractor: {message}");
        return 1;
    }

    private static string Scope(D2RInstallation install) =>
        install.HasCustomTargets
            ? $"into {install.ActiveTargets.Count} destinations"
            : $"into {install.ActiveTargets.FirstOrDefault()?.FolderPath ?? install.FolderPath}";

    private static bool Confirm(CommandLine cli, string question)
    {
        if (cli.Yes) return true;

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine($"{question} Refusing to assume; pass --yes to proceed non-interactively.");
            return false;
        }

        Console.Write($"{question} [y/N] ");
        string? answer = Console.ReadLine();
        return answer is not null
            && (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
             || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Applies command-line overrides on top of the stored preferences without saving them, so a
    /// one-off --international dede does not silently change what the GUI does next.
    /// </summary>
    private static AppPreferences Preferences(CommandLine cli)
    {
        var prefs = ManifestService.LoadPreferences();

        if (cli.NoInternational)
        {
            prefs.ExtractInternationalFiles = false;
        }
        else if (cli.International != null)
        {
            prefs.ExtractInternationalFiles = true;
            prefs.InternationalLanguage = cli.International;
        }

        if (cli.Verify) prefs.VerifyFileContents = true;
        return prefs;
    }

    private static void Refresh(D2RInstallation install)
    {
        var prefs = ManifestService.LoadPreferences();

        foreach (var target in install.EffectiveTargets)
        {
            var manifest = ManifestService.LoadManifest(target);
            target.RefreshState(manifest?.IsComplete, manifest?.InternationalExtracted,
                prefs.ExtractInternationalFiles, manifest?.InternationalLanguage, prefs.InternationalLanguage);
        }

        install.RefreshAggregateState();
    }

    private static string StateOf(D2RInstallation install) =>
        install.IsExtracted ? "extracted"
        : install.IsPartiallyExtracted ? "incomplete — run update"
        : "not extracted";

    /// <summary>
    /// Resolves a path argument as typed, expanding a leading <c>~</c>.
    ///
    /// <para>
    /// The shell expands it only when unquoted, and quoting is unavoidable here because the
    /// folder is always called "Diablo II Resurrected". A Steam library usually lives under
    /// $HOME, so without this the most natural command is the one that fails.
    /// </para>
    /// </summary>
    private static string ResolveInputPath(string input)
    {
        string path = input;

        if (path == "~")
        {
            path = Home();
        }
        else if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal))
        {
            path = Path.Combine(Home(), path[2..]);
        }

        return Path.GetFullPath(path);

        static string Home()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home) ? Environment.CurrentDirectory : home;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                      OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a target given as a 1-based index from 'list', a folder path, or omitted when
    /// only one installation is configured.
    /// </summary>
    private static bool TryResolve(string? target, List<D2RInstallation> installs,
                                   out D2RInstallation install, out int exitCode)
    {
        install = null!;
        exitCode = 0;

        if (installs.Count == 0)
        {
            exitCode = Fail("No installations configured. Add one with: d2rextractor add <folder>");
            return false;
        }

        if (target == null)
        {
            if (installs.Count == 1) { install = installs[0]; return true; }
            exitCode = Fail($"{installs.Count} installations configured — name one (see 'list').");
            return false;
        }

        if (int.TryParse(target, out int index))
        {
            if (index < 1 || index > installs.Count)
            {
                exitCode = Fail($"No installation [{index}]. There are {installs.Count}.");
                return false;
            }
            install = installs[index - 1];
            return true;
        }

        string path = ResolveInputPath(target);
        var match = installs.FirstOrDefault(i => PathsEqual(i.FolderPath, path));
        if (match == null)
        {
            exitCode = Fail($"Not a registered installation: {path}");
            return false;
        }

        install = match;
        return true;
    }
}
