using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using D2RExtractor.Models;
using D2RExtractor.Native;
using D2RExtractor.Services.Steam;

namespace D2RExtractor.Services;

/// <summary>
/// Progress report emitted during extraction.
/// </summary>
/// <summary>
/// IsEnumerating = true during the initial CascFindFirstFile phase (can take several minutes).
/// Show an indeterminate progress bar while this is true.
/// </summary>
public record ExtractionProgress(
    int FilesProcessed,
    int TotalFiles,
    string CurrentFile,
    long BytesProcessed,
    long TotalBytes,
    bool IsEnumerating = false);

/// <summary>
/// Core service that opens D2R's CASC storage via CascLib.dll and extracts
/// the game data folders to the installation directory.
/// </summary>
public class CascExtractorService
{
    /// <summary>
    /// CASC virtual-path prefixes that are extracted.
    /// These map to the "global", "hd", and "local" folders described in the guide.
    /// </summary>
    // CascLib returns virtual paths with a CASC namespace prefix: "data:data\global\…"
    // These must match what szFileName actually contains (confirmed via diagnostic logging).
    private static readonly string[] TargetPrefixes =
    {
        @"data:data\global\",
        @"data:data\hd\",
        @"data:data\local\"
    };

    /// <summary>
    /// Builds CASC virtual-path prefixes for the given language's locale files.
    /// Confirmed via CascDiagnostic: files live under two sub-prefixes:
    ///   data:locales\audio\{langcode}\  — FLAC dubbing audio
    ///   data:locales\data\{langcode}\   — text/localization data
    /// </summary>
    private static string[] GetInternationalPrefixes(string langCode)
    {
        string lc = langCode.ToLowerInvariant();
        return new[]
        {
            $@"data:locales\audio\{lc}\",
            $@"data:locales\data\{lc}\"
        };
    }

    // -----------------------------------------------------------------------
    // Extraction
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts the D2R data folders from CASC storage into the installation directory.
    ///
    /// Call from a background thread (or Task.Run). Progress is reported via <paramref name="progress"/>.
    /// The operation can be cancelled via <paramref name="ct"/>.
    ///
    /// On success the manifest is saved; on failure any partially-written files are left in place
    /// so the user can retry without restarting from scratch.
    /// </summary>
    public ExtractionManifest Extract(
        D2RInstallation installation,
        bool extractInternational,
        string? internationalLanguage,
        IProgress<ExtractionProgress>? progress,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string installPath = installation.FolderPath;

        log?.Invoke($"Opening storage at: {installPath}");

        using IExtractionBackend backend = CreateBackend(installPath, log);

        log?.Invoke("Opening file index — this can take a couple of minutes on first run, please wait…");
        progress?.Report(new ExtractionProgress(0, 0, "", 0, 0, IsEnumerating: true));

        // First pass: collect all matching file entries to get a total count.
        // For CascLib (Battle.net) this blocks while D2R's internal file index builds — the slow part.
        // The Steam static-container backend parses its file tree in a few seconds instead.
        // onIndexBuildComplete fires once the index is ready so we can log elapsed time and switch
        // the status text; onScanProgress fires periodically during the scan so the UI stays alive.
        Action<string>? onScanProgress = progress == null ? null : currentFile =>
            progress.Report(new ExtractionProgress(0, 0, currentFile, 0, 0, IsEnumerating: true));

        Action<long> onIndexBuildComplete = elapsedMs =>
        {
            log?.Invoke($"File index opened in {elapsedMs / 1000.0:F1}s — scanning entries…");
            progress?.Report(new ExtractionProgress(0, 0, "[scanning entries…]", 0, 0, IsEnumerating: true));
        };

        string[] prefixes = extractInternational && !string.IsNullOrEmpty(internationalLanguage)
            ? TargetPrefixes.Concat(GetInternationalPrefixes(internationalLanguage)).ToArray()
            : TargetPrefixes;
        var enumSw = System.Diagnostics.Stopwatch.StartNew();
        var files = backend.EnumerateMatching(prefixes, ct, onScanProgress, onIndexBuildComplete, log);
        enumSw.Stop();

        log?.Invoke($"Entry scan complete in {enumSw.ElapsedMilliseconds / 1000.0:F1}s — {files.Count:N0} matching files found.");

        // Throw here (regular method code, not an iterator) if the user cancelled.
        ct.ThrowIfCancellationRequested();

        progress?.Report(new ExtractionProgress(0, files.Count, "", 0, 0, IsEnumerating: false));

        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                "No matching files found in the game storage. " +
                "The listfile may be missing or the storage format is not recognised. " +
                "Ensure you are pointing at the correct D2R installation folder.");
        }

        var manifest = new ExtractionManifest { ExtractedAt = DateTime.UtcNow, IsComplete = false };

        ExtractFilesParallel(backend, files, installPath, installation, manifest, progress, log, ct);

        manifest.IsComplete = true;
        manifest.InternationalExtracted = extractInternational && !string.IsNullOrEmpty(internationalLanguage);
        manifest.InternationalLanguage = extractInternational ? internationalLanguage : null;
        ManifestService.SaveManifest(installation, manifest);
        log?.Invoke($"Extraction complete. {manifest.ExtractedFiles.Count:N0} files, {FormatBytes(manifest.TotalBytesExtracted)} written.");
        return manifest;
    }

    /// <summary>
    /// Selects the storage backend for an install: the native Steam static-container
    /// reader when a <c>data\.build.config</c> is present (Steam patch build 93236+),
    /// otherwise CascLib for classic CASC (Battle.net).
    /// </summary>
    private static IExtractionBackend CreateBackend(string installPath, Action<string>? log)
    {
        if (SteamStaticStorage.IsSteamStaticStorage(installPath))
        {
            log?.Invoke("Detected Steam static-container storage — using the native local reader (no internet required).");
            return new SteamStaticBackend(installPath, log);
        }
        log?.Invoke("Using CascLib storage backend (Battle.net / classic CASC).");
        return new CascLibBackend(installPath);
    }

    /// <summary>
    /// Extracts only the international (locales) CASC prefix and appends the results
    /// to an existing complete manifest. Called when the base has already been extracted
    /// but the international setting was enabled afterward.
    /// </summary>
    public ExtractionManifest ExtractInternationalOnly(
        D2RInstallation installation,
        ExtractionManifest existingManifest,
        string languageCode,
        IProgress<ExtractionProgress>? progress,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string installPath = installation.FolderPath;

        log?.Invoke("Opening storage for international files extraction…");
        log?.Invoke("Opening file index — this can take a couple of minutes on first run, please wait…");
        progress?.Report(new ExtractionProgress(0, 0, "", 0, 0, IsEnumerating: true));

        using IExtractionBackend backend = CreateBackend(installPath, log);

        Action<string>? onScanProgress = progress == null ? null : currentFile =>
            progress.Report(new ExtractionProgress(0, 0, currentFile, 0, 0, IsEnumerating: true));

        Action<long> onIndexBuildComplete = elapsedMs =>
        {
            log?.Invoke($"File index opened in {elapsedMs / 1000.0:F1}s — scanning for international files…");
            progress?.Report(new ExtractionProgress(0, 0, "[scanning entries…]", 0, 0, IsEnumerating: true));
        };

        var files = backend.EnumerateMatching(GetInternationalPrefixes(languageCode), ct, onScanProgress, onIndexBuildComplete, log);

        ct.ThrowIfCancellationRequested();

        // Zero files is not an error — this install may not have any language packs.
        if (files.Count == 0)
        {
            log?.Invoke($"No international files found for language '{languageCode}' — this installation may not have that language pack downloaded. Marking international extraction as complete.");
            existingManifest.InternationalExtracted = true;
            existingManifest.InternationalLanguage = languageCode;
            ManifestService.SaveManifest(installation, existingManifest);
            return existingManifest;
        }

        ExtractFilesParallel(backend, files, installPath, installation, existingManifest, progress, log, ct);

        existingManifest.InternationalExtracted = true;
        existingManifest.InternationalLanguage = languageCode;
        ManifestService.SaveManifest(installation, existingManifest);
        log?.Invoke($"International extraction complete. {files.Count:N0} files extracted for '{languageCode}'.");
        return existingManifest;
    }

    // -----------------------------------------------------------------------
    // Parallel extraction engine
    // -----------------------------------------------------------------------

    private const int ChunkSize = 1024 * 1024; // 1 MB read buffer

    /// <summary>
    /// Extracts <paramref name="files"/> via the given <paramref name="backend"/>.
    /// Storage reads are single-threaded (neither backend supports concurrent access);
    /// each file is streamed/decoded directly to disk. This method owns the shared
    /// bookkeeping — destination paths, directory creation, manifest, and progress —
    /// so both storage formats go through identical output logic.
    /// </summary>
    private static void ExtractFilesParallel(
        IExtractionBackend backend,
        List<(string VirtualPath, ulong FileSize)> files,
        string installPath,
        D2RInstallation installation,
        ExtractionManifest manifest,
        IProgress<ExtractionProgress>? progress,
        Action<string>? log,
        CancellationToken ct)
    {
        log?.Invoke($"Starting extraction of {files.Count:N0} files…");

        long totalBytes = files.Sum(f => (long)f.FileSize);
        long prevManifestBytes = manifest.TotalBytesExtracted;
        progress?.Report(new ExtractionProgress(0, files.Count, "", 0, totalBytes, IsEnumerating: false));

        backend.PrepareExtraction(log);

        try
        {
            byte[] buffer = new byte[ChunkSize];
            int processed = 0;
            long bytesProcessed = 0;
            int warnCount = 0;
            var progressSw = System.Diagnostics.Stopwatch.StartNew();

            foreach (var (virtualPath, fileSize) in files)
            {
                ct.ThrowIfCancellationRequested();

                string fsRelPath = StripCascNamespace(virtualPath);
                string destPath = Path.Combine(installPath, fsRelPath);
                string? destDir = Path.GetDirectoryName(destPath);
                if (destDir != null)
                    Directory.CreateDirectory(destDir);

                bool extracted = backend.ExtractFile(virtualPath, destPath, buffer);

                if (extracted)
                    manifest.ExtractedFiles.Add(fsRelPath);
                else
                    warnCount++;

                bytesProcessed += (long)fileSize;
                processed++;

                // Throttle progress reporting to ~4 updates/sec to avoid flooding the UI thread.
                if (progressSw.ElapsedMilliseconds >= 250)
                {
                    progress?.Report(new ExtractionProgress(
                        processed, files.Count, virtualPath, bytesProcessed, totalBytes));
                    progressSw.Restart();
                }

                if (processed % 500 == 0)
                    ManifestService.SaveManifest(installation, manifest);
            }

            // Final progress update.
            progress?.Report(new ExtractionProgress(
                processed, files.Count, "", bytesProcessed, totalBytes));

            manifest.TotalBytesExtracted = prevManifestBytes + bytesProcessed;

            if (warnCount > 0)
                log?.Invoke($"Extraction complete with {warnCount} file(s) skipped (could not be opened or sized — these may be CDN-only files not present locally).");
        }
        finally
        {
            backend.FinishExtraction();
        }
    }

    // -----------------------------------------------------------------------
    // Undo (remove extracted files)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Removes all files listed in the extraction manifest and deletes the manifest itself.
    /// Runs synchronously; call from Task.Run if needed.
    /// </summary>
    public void UndoExtraction(
        D2RInstallation installation,
        IProgress<ExtractionProgress>? progress,
        Action<string>? log,
        CancellationToken ct)
    {
        var manifest = ManifestService.LoadManifest(installation)
            ?? throw new InvalidOperationException("No extraction manifest found. Nothing to undo.");

        log?.Invoke($"Undoing extraction: {manifest.ExtractedFiles.Count:N0} files to remove…");

        int total = manifest.ExtractedFiles.Count;
        int processed = 0;

        foreach (string relativePath in manifest.ExtractedFiles)
        {
            ct.ThrowIfCancellationRequested();

            string fullPath = Path.Combine(installation.FolderPath, relativePath);
            if (File.Exists(fullPath))
            {
                try { File.Delete(fullPath); }
                catch (Exception ex)
                {
                    log?.Invoke($"  WARN: Could not delete '{relativePath}': {ex.Message}");
                }
            }

            processed++;
            progress?.Report(new ExtractionProgress(processed, total, relativePath, processed, total));
        }

        // Remove now-empty directories under the data\ tree.
        // International files now extract into data\ (not locales\), so cleaning
        // the three target prefix directories covers everything.
        foreach (string prefix in TargetPrefixes)
        {
            string fsPrefix = StripCascNamespace(prefix);
            string dir = Path.Combine(installation.FolderPath, fsPrefix.TrimEnd('\\'));
            RemoveEmptyDirectories(dir, log);
        }
        // Also clean up any old-style locales\ directory from pre-v1.1.4 extractions.
        string oldLocalesDir = Path.Combine(installation.FolderPath, "locales");
        RemoveEmptyDirectories(oldLocalesDir, log);

        ManifestService.DeleteManifest(installation);
        log?.Invoke("Undo complete. Extracted files have been removed.");
    }

    private static void RemoveEmptyDirectories(string path, Action<string>? log)
    {
        if (!Directory.Exists(path)) return;

        foreach (string subDir in Directory.GetDirectories(path))
            RemoveEmptyDirectories(subDir, log);

        if (Directory.GetFiles(path).Length == 0 && Directory.GetDirectories(path).Length == 0)
        {
            try { Directory.Delete(path); }
            catch (Exception ex)
            {
                log?.Invoke($"  WARN: Could not remove directory '{path}': {ex.Message}");
            }
        }
    }

    // -----------------------------------------------------------------------
    // Pre-flight checks
    // -----------------------------------------------------------------------

    /// <summary>
    /// Checks whether the given folder looks like a D2R installation.
    /// Returns null on success, or an error message on failure.
    /// </summary>
    public static string? ValidateInstallationFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return "Folder does not exist.";

        // Steam static-container installs (patch build 93236+) have a flat data\ folder
        // with a .build.config instead of the classic Data\indices layout.
        if (SteamStaticStorage.IsSteamStaticStorage(folderPath))
            return null;

        // Classic CASC (Battle.net) stores index files under "Data\indices".
        string indicesPath = Path.Combine(folderPath, "Data", "indices");
        if (!Directory.Exists(indicesPath))
            return "The selected folder does not appear to be a D2R installation. " +
                   "Expected a 'Data\\indices' subfolder (Battle.net) or a 'data\\.build.config' (Steam). " +
                   "Please select the root D2R installation folder.";

        return null;
    }

    /// <summary>
    /// Estimates the available free space on the drive containing <paramref name="folderPath"/>
    /// and warns if less than <paramref name="requiredBytes"/> are available.
    /// </summary>
    public static string? CheckDiskSpace(string folderPath, long requiredBytes = 48L * 1024 * 1024 * 1024)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(folderPath)!);
            if (drive.AvailableFreeSpace < requiredBytes)
            {
                return $"Low disk space warning: only {FormatBytes(drive.AvailableFreeSpace)} free on " +
                       $"{drive.Name}. Extraction requires approximately {FormatBytes(requiredBytes)}.";
            }
        }
        catch { /* Ignore drive-info failures */ }
        return null;
    }

    /// <summary>
    /// Strips the CASC VFS namespace prefix from a virtual path so it can be used as a
    /// filesystem-relative path.
    ///
    /// For base files:
    ///   <c>"data:data\global\allcofs.bin"</c> → <c>"data\global\allcofs.bin"</c>
    ///
    /// For locale files, the <c>locales\{type}\{langcode}\</c> prefix is also stripped so
    /// the files land in the game's <c>data\</c> tree where D2R expects them in -direct mode:
    ///   <c>"data:locales\audio\itit\data\hd\local\sfx\..."</c> → <c>"data\hd\local\sfx\..."</c>
    ///   <c>"data:locales\data\dede\data\local\lng\..."</c> → <c>"data\local\lng\..."</c>
    /// </summary>
    private static string StripCascNamespace(string cascPath)
    {
        int i = cascPath.IndexOf(':');
        string afterNamespace = i >= 0 ? cascPath.Substring(i + 1) : cascPath;

        // Locale paths: locales\{type}\{langcode}\data\... → strip the first 3 segments.
        if (afterNamespace.StartsWith(@"locales\", StringComparison.OrdinalIgnoreCase))
        {
            // Find the 3rd backslash: locales\audio\itit\...
            int pos = 0;
            for (int seg = 0; seg < 3 && pos < afterNamespace.Length; seg++)
            {
                int next = afterNamespace.IndexOf('\\', pos);
                if (next < 0) return afterNamespace; // malformed — return as-is
                pos = next + 1;
            }
            return afterNamespace.Substring(pos);
        }

        return afterNamespace;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / 1024.0:F1} KB";
    }
}

