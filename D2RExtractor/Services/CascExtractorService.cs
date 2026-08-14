using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using D2RExtractor.Models;
using D2RExtractor.Native;
using D2RExtractor.Services.Steam;

namespace D2RExtractor.Services;

/// <summary>
/// Which stage of an operation a progress report describes. Only <see cref="Writing"/> and
/// <see cref="Removing"/> have a meaningful completion ratio; the others should show an
/// indeterminate progress bar.
/// </summary>
public enum ExtractionPhase
{
    /// <summary>Opening the storage and listing its files. Minutes on a Battle.net install.</summary>
    Enumerating,

    /// <summary>Diffing the archives against what is already on disk. Writes nothing.</summary>
    Comparing,

    /// <summary>Checksumming extracted files against their recorded content keys. Writes nothing.</summary>
    Verifying,

    /// <summary>Writing files to disk.</summary>
    Writing,

    /// <summary>Deleting files that are no longer in the archives.</summary>
    Removing,
}

/// <summary>
/// Progress report emitted during extraction, update and undo.
/// </summary>
public record ExtractionProgress(
    int FilesProcessed,
    int TotalFiles,
    string CurrentFile,
    long BytesProcessed,
    long TotalBytes,
    ExtractionPhase Phase = ExtractionPhase.Writing)
{
    /// <summary>
    /// True while the operation has no meaningful percentage to report, so the UI should show an
    /// indeterminate bar.
    /// </summary>
    public bool IsEnumerating => Phase is ExtractionPhase.Enumerating;
}

/// <summary>
/// What an <see cref="CascExtractorService.UpdateExtraction"/> pass actually did.
/// </summary>
/// <param name="FilesWritten">Files written because they were new, changed, missing or damaged.</param>
/// <param name="BytesWritten">Total bytes those files accounted for.</param>
/// <param name="FilesRemoved">Extracted files deleted because the archives no longer contain them.</param>
/// <param name="FilesUnchanged">Files left completely untouched — the point of the exercise.</param>
public record UpdateSummary(int FilesWritten, long BytesWritten, int FilesRemoved, int FilesUnchanged);

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

        string[] prefixes = BuildPrefixes(extractInternational, internationalLanguage);
        var files = DeduplicateByOutputPath(
            EnumerateStorage(backend, prefixes, progress, log, ct), log);

        var manifest = new ExtractionManifest
        {
            ManifestVersion = ExtractionManifest.CurrentVersion,
            ExtractedAt = DateTime.UtcNow,
            IsComplete = false,
            KeySource = backend.KeySource,
            EntryFile = ExtractionManifest.DefaultEntryFile,
        };

        // Persist the incomplete-extraction marker before writing a single file. Without it, a
        // crash early in the run would leave extracted files on disk with no manifest naming them,
        // and Undo would have nothing to work from.
        ManifestService.ResetEntries(installation, manifest);
        ManifestService.SaveManifest(installation, manifest);

        using (var entries = ManifestService.OpenEntryWriter(installation, manifest))
        {
            ExtractFiles(backend, files, installPath, installation, manifest, entries, progress, log, ct);
        }

        manifest.IsComplete = true;
        manifest.InternationalExtracted = extractInternational && !string.IsNullOrEmpty(internationalLanguage);
        manifest.InternationalLanguage = extractInternational ? internationalLanguage : null;
        ManifestService.SaveManifest(installation, manifest);
        log?.Invoke($"Extraction complete. {manifest.EntryCount:N0} files, {FormatBytes(manifest.TotalBytesExtracted)} written.");
        return manifest;
    }

    /// <summary>
    /// The set of storage prefixes an operation should cover, given the current international
    /// settings. Shared by extract and update so both always look at exactly the same file set.
    /// </summary>
    private static string[] BuildPrefixes(bool extractInternational, string? internationalLanguage) =>
        extractInternational && !string.IsNullOrEmpty(internationalLanguage)
            ? TargetPrefixes.Concat(GetInternationalPrefixes(internationalLanguage)).ToArray()
            : TargetPrefixes;

    /// <summary>
    /// Runs the backend's enumeration pass with progress reporting, and rejects an empty result.
    ///
    /// <para>
    /// For CascLib (Battle.net) this blocks while D2R's internal file index builds — the slow part,
    /// several minutes on first run. The Steam static-container backend parses its file tree in a
    /// few seconds instead. <c>onIndexBuildComplete</c> fires once the index is ready so we can log
    /// elapsed time and switch the status text; <c>onScanProgress</c> fires periodically during the
    /// scan so the UI stays alive.
    /// </para>
    /// </summary>
    private static List<StorageEntry> EnumerateStorage(
        IExtractionBackend backend,
        string[] prefixes,
        IProgress<ExtractionProgress>? progress,
        Action<string>? log,
        CancellationToken ct)
    {
        log?.Invoke("Opening file index — this can take a couple of minutes on first run, please wait…");
        progress?.Report(new ExtractionProgress(0, 0, "", 0, 0, ExtractionPhase.Enumerating));

        Action<string>? onScanProgress = progress == null ? null : currentFile =>
            progress.Report(new ExtractionProgress(0, 0, currentFile, 0, 0, ExtractionPhase.Enumerating));

        Action<long> onIndexBuildComplete = elapsedMs =>
        {
            log?.Invoke($"File index opened in {elapsedMs / 1000.0:F1}s — scanning entries…");
            progress?.Report(new ExtractionProgress(0, 0, "[scanning entries…]", 0, 0, ExtractionPhase.Enumerating));
        };

        var enumSw = System.Diagnostics.Stopwatch.StartNew();
        var files = backend.EnumerateMatching(prefixes, ct, onScanProgress, onIndexBuildComplete, log);
        enumSw.Stop();

        log?.Invoke($"Entry scan complete in {enumSw.ElapsedMilliseconds / 1000.0:F1}s — {files.Count:N0} matching files found.");

        // Throw here (regular method code, not an iterator) if the user cancelled.
        ct.ThrowIfCancellationRequested();

        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                "No matching files found in the game storage. " +
                "The listfile may be missing or the storage format is not recognised. " +
                "Ensure you are pointing at the correct D2R installation folder.");
        }

        return files;
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
    /// Collapses entries that would be written to the same file, keeping the locale one.
    ///
    /// <para>
    /// International files deliberately land on top of their base-English counterparts:
    /// <c>data:locales\data\itit\data\local\lng\…</c> and <c>data:data\local\lng\…</c> both strip
    /// to the same <c>data\local\lng\…</c>. Keeping both means writing that file twice and letting
    /// storage enumeration order decide which content survives. During an update it is worse than
    /// wasteful — the two entries carry different content keys, so the file can be written from one
    /// and recorded as the other, leaving the manifest describing content that is not on disk and a
    /// later update unable to notice the discrepancy.
    /// </para>
    /// <para>
    /// Resolving it up front makes the result deterministic — the selected language wins, which is
    /// the whole point of the setting — and halves the writes for every overridden file.
    /// </para>
    /// </summary>
    private static List<StorageEntry> DeduplicateByOutputPath(List<StorageEntry> files, Action<string>? log)
    {
        var byOutput = new Dictionary<string, StorageEntry>(files.Count, StringComparer.OrdinalIgnoreCase);
        int overridden = 0;

        foreach (StorageEntry file in files)
        {
            string relPath = StripCascNamespace(file.VirtualPath);
            if (byOutput.TryGetValue(relPath, out StorageEntry existing))
            {
                // The locale entry wins; if neither or both are locale entries, the later one does.
                if (!IsLocaleEntry(file.VirtualPath) && IsLocaleEntry(existing.VirtualPath))
                    continue;
                overridden++;
            }
            byOutput[relPath] = file;
        }

        if (overridden > 0)
            log?.Invoke($"{overridden:N0} base file(s) are replaced by the selected language — each will be written once.");

        return byOutput.Values.ToList();
    }

    private static bool IsLocaleEntry(string virtualPath)
    {
        int i = virtualPath.IndexOf(':');
        string afterNamespace = i >= 0 ? virtualPath[(i + 1)..] : virtualPath;
        return afterNamespace.StartsWith(@"locales\", StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Extraction engine
    // -----------------------------------------------------------------------

    private const int ChunkSize = 1024 * 1024; // 1 MB read buffer

    /// <summary>
    /// Extracts <paramref name="files"/> via the given <paramref name="backend"/>.
    /// Storage reads are single-threaded (neither backend supports concurrent access);
    /// each file is streamed/decoded directly to disk. This method owns the shared
    /// bookkeeping — destination paths, directory creation, manifest, and progress —
    /// so both storage formats go through identical output logic.
    /// </summary>
    private static void ExtractFiles(
        IExtractionBackend backend,
        IReadOnlyList<StorageEntry> files,
        string installPath,
        D2RInstallation installation,
        ExtractionManifest manifest,
        ManifestService.EntryWriter entries,
        IProgress<ExtractionProgress>? progress,
        Action<string>? log,
        CancellationToken ct)
    {
        log?.Invoke($"Starting extraction of {files.Count:N0} files…");

        long totalBytes = files.Sum(f => (long)f.FileSize);
        long prevManifestBytes = manifest.TotalBytesExtracted;
        int prevEntryCount = manifest.EntryCount;
        progress?.Report(new ExtractionProgress(0, files.Count, "", 0, totalBytes));

        backend.PrepareExtraction(log);

        try
        {
            byte[] buffer = new byte[ChunkSize];
            int processed = 0;
            long bytesProcessed = 0;
            int warnCount = 0;
            var progressSw = System.Diagnostics.Stopwatch.StartNew();

            foreach (StorageEntry file in files)
            {
                ct.ThrowIfCancellationRequested();

                string fsRelPath = StripCascNamespace(file.VirtualPath);
                string destPath = Path.Combine(installPath, fsRelPath);
                string? destDir = Path.GetDirectoryName(destPath);
                if (destDir != null)
                    Directory.CreateDirectory(destDir);

                bool extracted = backend.ExtractFile(file.VirtualPath, destPath, buffer);

                if (extracted)
                    entries.Append(new ManifestEntry(fsRelPath, file.ContentKey, (long)file.FileSize));
                else
                    warnCount++;

                bytesProcessed += (long)file.FileSize;
                processed++;

                // Throttle progress reporting to ~4 updates/sec to avoid flooding the UI thread.
                if (progressSw.ElapsedMilliseconds >= 250)
                {
                    progress?.Report(new ExtractionProgress(
                        processed, files.Count, file.VirtualPath, bytesProcessed, totalBytes));
                    progressSw.Restart();
                }
            }

            // Final progress update.
            progress?.Report(new ExtractionProgress(
                processed, files.Count, "", bytesProcessed, totalBytes));

            manifest.TotalBytesExtracted = prevManifestBytes + bytesProcessed;
            manifest.EntryCount = prevEntryCount + entries.Appended;

            if (warnCount > 0)
                log?.Invoke($"Extraction complete with {warnCount} file(s) skipped (could not be opened or sized — these may be CDN-only files not present locally).");
        }
        finally
        {
            // Get every record of a written file to disk even when the run is cancelled or fails —
            // anything not in the sidecar is a file Undo would leave behind.
            try { entries.Flush(); } catch (IOException) { }
            manifest.EntryCount = prevEntryCount + entries.Appended;
            backend.FinishExtraction();
        }
    }

    // -----------------------------------------------------------------------
    // Update (rewrite only what changed)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Brings an existing extraction back in line with the game archives without rewriting files
    /// that have not changed.
    ///
    /// <para>
    /// Each archive entry is compared against the manifest by content key and against the disk by
    /// existence and size; only genuine differences are written. Files the manifest lists that the
    /// archives no longer contain are deleted, so the extracted tree keeps matching the archives
    /// after a patch removes an asset.
    /// </para>
    /// <para>
    /// This is also how an interrupted extraction is resumed, and how an international-language
    /// change is applied: both are just diffs. Unlike a fresh extraction it never deletes first,
    /// so a cancelled update leaves a consistent tree that Undo can still fully remove.
    /// </para>
    /// </summary>
    /// <param name="verifyContents">
    /// Additionally checksum every extracted file against its recorded content key. Reads and
    /// hashes the whole extraction (tens of GB) but writes nothing extra; only possible when the
    /// storage supplies content hashes rather than encoding keys.
    /// </param>
    public UpdateSummary UpdateExtraction(
        D2RInstallation installation,
        ExtractionManifest manifest,
        bool extractInternational,
        string? internationalLanguage,
        bool verifyContents,
        IProgress<ExtractionProgress>? progress,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string installPath = installation.FolderPath;

        log?.Invoke($"Opening storage at: {installPath}");
        using IExtractionBackend backend = CreateBackend(installPath, log);

        string[] prefixes = BuildPrefixes(extractInternational, internationalLanguage);
        var files = DeduplicateByOutputPath(
            EnumerateStorage(backend, prefixes, progress, log, ct), log);

        // ---- Gather the current state of the extracted tree -----------------
        progress?.Report(new ExtractionProgress(0, files.Count, "[comparing…]", 0, 0, ExtractionPhase.Comparing));

        var onDisk = ScanExtractedFiles(installPath, installation, manifest, log, ct);

        var recorded = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestEntry entry in ManifestService.EnumerateEntries(installation, manifest))
            recorded[entry.RelPath] = entry;

        log?.Invoke($"Comparing {files.Count:N0} archive entries against {recorded.Count:N0} recorded " +
                    $"and {onDisk.Count:N0} on-disk file(s)…");

        // Keys from a different storage format mean nothing here — a manifest written against
        // Battle.net CKeys cannot be compared to Steam encoding keys, and vice versa.
        bool keysComparable = !manifest.IsLegacySchema
                              && backend.KeySource != Models.KeySource.None
                              && string.Equals(manifest.KeySource, backend.KeySource, StringComparison.Ordinal);

        if (!keysComparable && !manifest.IsLegacySchema && manifest.KeySource != backend.KeySource)
        {
            log?.Invoke($"Recorded content keys came from '{manifest.KeySource}' but this storage supplies " +
                        $"'{backend.KeySource}' — falling back to size comparison for this update.");
        }

        // Content keys can only be checked against a file on disk when they are hashes of the
        // file's content; encoding keys identify the compressed blob and cannot be reproduced
        // from the extracted file.
        bool canVerifyOnDisk = Models.KeySource.IsContentMd5(backend.KeySource);
        bool verifyPass = (verifyContents || manifest.IsLegacySchema) && canVerifyOnDisk;

        if (manifest.IsLegacySchema)
        {
            log?.Invoke(canVerifyOnDisk
                ? "This extraction predates content tracking. Checking the extracted files against the " +
                  "archives once so only genuinely stale files are rewritten — this reads the extraction " +
                  "but writes nothing extra."
                : "This extraction predates content tracking, and this storage cannot verify extracted " +
                  "files directly. Falling back to size comparison; a file that changed without changing " +
                  "size will not be detected until the next game patch rewrites it.");
        }
        else if (verifyContents && !canVerifyOnDisk)
        {
            log?.Invoke("Content verification was requested but this storage supplies encoding keys, " +
                        "which cannot be checked against extracted files. Continuing without verification.");
        }

        // ---- Classify -------------------------------------------------------
        var toWrite = new List<StorageEntry>();
        var finalEntries = new Dictionary<string, ManifestEntry>(files.Count, StringComparer.OrdinalIgnoreCase);
        var seenInStorage = new HashSet<string>(files.Count, StringComparer.OrdinalIgnoreCase);

        int unchanged = 0, verified = 0;
        bool backfilledBySize = false;
        var compareSw = System.Diagnostics.Stopwatch.StartNew();
        int examined = 0;

        foreach (StorageEntry file in files)
        {
            ct.ThrowIfCancellationRequested();
            examined++;

            string relPath = StripCascNamespace(file.VirtualPath);
            seenInStorage.Add(relPath);

            bool onDiskHasFile = onDisk.TryGetValue(relPath, out long diskSize);
            bool recordedHasFile = recorded.TryGetValue(relPath, out ManifestEntry prior);
            bool keyKnown = keysComparable && recordedHasFile && prior.Key != null && file.ContentKey != null;
            bool needsWrite;

            if (!recordedHasFile)
            {
                needsWrite = true;                              // new file
            }
            else if (!onDiskHasFile || diskSize != (long)file.FileSize)
            {
                needsWrite = true;                              // missing, truncated or resized
            }
            else if (keyKnown && !string.Equals(prior.Key, file.ContentKey, StringComparison.Ordinal))
            {
                needsWrite = true;                              // the archives changed this file
            }
            else if (verifyPass && file.ContentKey != null)
            {
                // Everything cheap says this file is fine. Verification is what catches the cases
                // the cheap checks structurally cannot see — a file corrupted or edited outside the
                // app, whose size never changed — so it runs whether or not a recorded key matched.
                string? actual = TryHashFile(Path.Combine(installPath, relPath));
                verified++;
                needsWrite = actual == null
                             || !string.Equals(actual, file.ContentKey, StringComparison.Ordinal);
            }
            else if (!keyKnown)
            {
                // Right size, but no comparable key and no way to verify — assume unchanged.
                needsWrite = false;
                backfilledBySize = true;
            }
            else
            {
                needsWrite = false;                             // recorded key matches the archives
            }

            if (needsWrite)
                toWrite.Add(file);
            else
                unchanged++;

            // Record the archive's key either way, so the next update has a comparable baseline.
            finalEntries[relPath] = new ManifestEntry(relPath, file.ContentKey, (long)file.FileSize);

            if (compareSw.ElapsedMilliseconds >= 250)
            {
                progress?.Report(new ExtractionProgress(
                    examined, files.Count, relPath, 0, 0,
                    verifyPass ? ExtractionPhase.Verifying : ExtractionPhase.Comparing));
                compareSw.Restart();
            }
        }

        // ---- Files the archives no longer contain ---------------------------
        var orphans = recorded.Keys.Where(p => !seenInStorage.Contains(p)).ToList();

        log?.Invoke($"Comparison complete — {toWrite.Count:N0} to write, {unchanged:N0} unchanged, " +
                    $"{orphans.Count:N0} to remove." +
                    (verified > 0 ? $" ({verified:N0} file(s) checksummed.)" : string.Empty));

        // ---- Apply ----------------------------------------------------------
        manifest.ManifestVersion = ExtractionManifest.CurrentVersion;
        manifest.KeySource = backend.KeySource;
        manifest.IsComplete = false;
        ManifestService.SaveManifest(installation, manifest);

        long bytesWritten = 0;
        if (toWrite.Count > 0)
        {
            // Append as files are written: a cancelled update must still leave every file it
            // created recorded, or Undo would strand them.
            using var entries = ManifestService.OpenEntryWriter(installation, manifest);
            ExtractFiles(backend, toWrite, installPath, installation, manifest, entries, progress, log, ct);
            bytesWritten = toWrite.Sum(f => (long)f.FileSize);
        }

        int removed = RemoveOrphans(installPath, orphans, progress, log, ct);

        // Now that every write has landed, replace the file list in one atomic pass.
        ManifestService.WriteAllEntries(installation, manifest, finalEntries.Values);

        foreach (string prefix in TargetPrefixes)
            RemoveEmptyDirectories(Path.Combine(installPath, StripCascNamespace(prefix).TrimEnd('\\')), log);

        manifest.TotalBytesExtracted = finalEntries.Values.Sum(e => Math.Max(e.Size, 0));
        manifest.ExtractedAt = DateTime.UtcNow;
        manifest.KeysBackfilledBySize = backfilledBySize;
        manifest.InternationalExtracted = extractInternational && !string.IsNullOrEmpty(internationalLanguage);
        manifest.InternationalLanguage = extractInternational ? internationalLanguage : null;
        manifest.IsComplete = true;
        ManifestService.SaveManifest(installation, manifest);

        var summary = new UpdateSummary(toWrite.Count, bytesWritten, removed, unchanged);
        log?.Invoke($"Update complete — {summary.FilesWritten:N0} written ({FormatBytes(summary.BytesWritten)}), " +
                    $"{summary.FilesUnchanged:N0} unchanged, {summary.FilesRemoved:N0} removed.");
        return summary;
    }

    /// <summary>
    /// Builds a relative-path → size map of everything currently under the installation's
    /// <c>data\</c> folder.
    ///
    /// <para>
    /// One directory walk rather than a stat per manifest entry: the enumeration hands back each
    /// file's length from the same directory record that lists it, so ~150,000 files cost a few
    /// seconds instead of a few hundred thousand separate metadata calls.
    /// </para>
    /// </summary>
    private static Dictionary<string, long> ScanExtractedFiles(
        string installPath,
        D2RInstallation installation,
        ExtractionManifest manifest,
        Action<string>? log,
        CancellationToken ct)
    {
        var result = new Dictionary<string, long>(220_000, StringComparer.OrdinalIgnoreCase);

        string dataDir = Path.Combine(installPath, "data");
        if (!Directory.Exists(dataDir))
            return result;

        // The manifest and its sidecar live inside the folder being scanned; they are bookkeeping,
        // not extracted content.
        string manifestPath = installation.ManifestPath;
        string entryPath = ManifestService.GetEntryFilePath(installation, manifest);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (FileInfo fi in new DirectoryInfo(dataDir).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();

            if (fi.FullName.Equals(manifestPath, StringComparison.OrdinalIgnoreCase) ||
                fi.FullName.Equals(entryPath, StringComparison.OrdinalIgnoreCase))
                continue;

            result[Path.GetRelativePath(installPath, fi.FullName)] = fi.Length;
        }
        sw.Stop();

        log?.Invoke($"Scanned {result.Count:N0} extracted file(s) in {sw.ElapsedMilliseconds / 1000.0:F1}s.");
        return result;
    }

    /// <summary>
    /// Deletes files the archives no longer contain. A patch that removes an asset would otherwise
    /// leave it behind, and the game would keep loading it in <c>-direct</c> mode.
    /// </summary>
    private static int RemoveOrphans(
        string installPath,
        List<string> orphans,
        IProgress<ExtractionProgress>? progress,
        Action<string>? log,
        CancellationToken ct)
    {
        if (orphans.Count == 0)
            return 0;

        int removed = 0, processed = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (string relPath in orphans)
        {
            ct.ThrowIfCancellationRequested();
            processed++;

            string fullPath = Path.Combine(installPath, relPath);
            if (File.Exists(fullPath))
            {
                try
                {
                    File.Delete(fullPath);
                    removed++;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"  WARN: Could not remove '{relPath}': {ex.Message}");
                }
            }

            if (sw.ElapsedMilliseconds >= 250)
            {
                progress?.Report(new ExtractionProgress(
                    processed, orphans.Count, relPath, 0, 0, ExtractionPhase.Removing));
                sw.Restart();
            }
        }

        return removed;
    }

    /// <summary>
    /// MD5 of a file on disk as lower-case hex, or null if it cannot be read. Comparable directly
    /// against a CASC content key, which is the MD5 of the file's decoded content.
    /// </summary>
    private static string? TryHashFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1024 * 1024, useAsync: false);
            return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
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

        // Stream the list rather than materialising ~150,000 paths, and take the count from the
        // header so progress has a total without a second pass.
        int total = manifest.IsLegacySchema
            ? manifest.ExtractedFiles?.Count ?? 0
            : manifest.EntryCount;

        log?.Invoke($"Undoing extraction: {total:N0} files to remove…");

        int processed = 0;
        var progressSw = System.Diagnostics.Stopwatch.StartNew();

        foreach (ManifestEntry entry in ManifestService.EnumerateEntries(installation, manifest))
        {
            ct.ThrowIfCancellationRequested();

            string fullPath = Path.Combine(installation.FolderPath, entry.RelPath);
            if (File.Exists(fullPath))
            {
                try { File.Delete(fullPath); }
                catch (Exception ex)
                {
                    log?.Invoke($"  WARN: Could not delete '{entry.RelPath}': {ex.Message}");
                }
            }

            processed++;
            if (progressSw.ElapsedMilliseconds >= 250)
            {
                progress?.Report(new ExtractionProgress(
                    processed, Math.Max(total, processed), entry.RelPath, processed, total,
                    ExtractionPhase.Removing));
                progressSw.Restart();
            }
        }

        progress?.Report(new ExtractionProgress(
            processed, Math.Max(total, processed), "", processed, total, ExtractionPhase.Removing));

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
    // Operation planning
    // -----------------------------------------------------------------------

    /// <summary>What should happen when the user presses an installation's primary action button.</summary>
    public enum OperationKind
    {
        /// <summary>Nothing has been extracted yet — full extraction.</summary>
        Extract,

        /// <summary>An extraction exists; bring it in line with the archives, writing only differences.</summary>
        Update,

        /// <summary>Undo the extraction.</summary>
        Undo,
    }

    /// <summary>
    /// Decides which operation an installation needs, given its manifest and the current settings.
    ///
    /// <para>
    /// Single source of truth: the model uses it to label and enable the primary button, and the
    /// window uses it to pick the method to call. When those were computed separately they could
    /// disagree, and the disagreement was invisible until a button did the wrong thing.
    /// </para>
    /// </summary>
    /// <param name="manifest">The installation's manifest, or null when it has none.</param>
    /// <param name="internationalEnabled">Whether international extraction is currently switched on.</param>
    /// <param name="preferredLanguage">The language currently selected in preferences.</param>
    public static OperationKind PlanOperation(
        ExtractionManifest? manifest, bool internationalEnabled, string? preferredLanguage)
    {
        // No manifest at all: nothing has been extracted.
        if (manifest == null)
            return OperationKind.Extract;

        // Anything else — interrupted, out of date, or wrong language — is a diff against what is
        // already on disk, which is both faster and gentler on the drive than starting over.
        return OperationKind.Update;
    }

    /// <summary>
    /// True when the manifest satisfies the current international setting (extracted, and in the
    /// language the user has selected).
    /// </summary>
    public static bool IsInternationalSatisfied(
        ExtractionManifest? manifest, bool internationalEnabled, string? preferredLanguage)
    {
        if (!internationalEnabled)
            return true;

        return manifest?.InternationalExtracted == true
               && string.Equals(manifest.InternationalLanguage, preferredLanguage,
                   StringComparison.OrdinalIgnoreCase);
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

