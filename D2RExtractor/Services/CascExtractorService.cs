using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using D2RExtractor.Models;
using D2RExtractor.Native;

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
        IProgress<ExtractionProgress>? progress,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string installPath = installation.FolderPath;

        log?.Invoke($"Opening CASC storage at: {installPath}");

        if (!CascLib.CascOpenStorage(installPath, 0, out IntPtr hStorage) || hStorage == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"CascOpenStorage failed (Win32 error {err}). " +
                $"Ensure '{installPath}' is a valid D2R installation folder.");
        }

        try
        {
            log?.Invoke("Opening CASC file index — this can take 2–3 minutes on first run, please wait…");
            progress?.Report(new ExtractionProgress(0, 0, "", 0, 0, IsEnumerating: true));

            // First pass: collect all matching file entries to get a total count.
            // CascFindFirstFile blocks while it builds D2R's internal file index — this is the slow part.
            // onIndexBuildComplete fires once CascFindFirstFile returns so we can log elapsed time and
            // switch the status text before the per-file scan loop begins.
            // The onScanProgress callback fires every ~500 ms from INSIDE the iterator (even for
            // non-matching files), so the UI updates throughout — not just when matching files are yielded.
            // Cancellation is detected post-loop (normal method code) so the debugger traces the
            // OperationCanceledException cleanly back to the catch handler in RunExtractAsync.
            var files = new List<(string VirtualPath, ulong FileSize)>();

            Action<string>? onScanProgress = progress == null ? null : currentFile =>
                progress.Report(new ExtractionProgress(
                    files.Count, 0, currentFile, 0, 0, IsEnumerating: true));

            Action<long> onIndexBuildComplete = elapsedMs =>
            {
                log?.Invoke($"File index opened in {elapsedMs / 1000.0:F1}s — scanning entries…");
                progress?.Report(new ExtractionProgress(0, 0, "[scanning entries…]", 0, 0, IsEnumerating: true));
            };

            var enumSw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var entry in CascLib.EnumerateFiles(hStorage, TargetPrefixes, ct, onScanProgress, onIndexBuildComplete, log))
                files.Add(entry);
            enumSw.Stop();

            log?.Invoke($"Entry scan complete in {enumSw.ElapsedMilliseconds / 1000.0:F1}s — {files.Count:N0} matching files found.");

            // Throw here (regular method code, not an iterator) if the user cancelled.
            ct.ThrowIfCancellationRequested();

            progress?.Report(new ExtractionProgress(0, files.Count, "", 0, 0, IsEnumerating: false));

            if (files.Count == 0)
            {
                throw new InvalidOperationException(
                    "No matching files found in CASC storage. " +
                    "The listfile may be missing or the CASC format is not recognised. " +
                    "Ensure you are pointing at the correct D2R installation folder.");
            }

            log?.Invoke($"Starting extraction of {files.Count:N0} files…");

            long totalBytes = files.Sum(f => (long)f.FileSize);
            var manifest = new ExtractionManifest { ExtractedAt = DateTime.UtcNow, IsComplete = false };

            int processed = 0;
            long bytesProcessed = 0;

            foreach (var (virtualPath, fileSize) in files)
            {
                ct.ThrowIfCancellationRequested();

                // Strip the CASC namespace (e.g. "data:data\global\foo" → "data\global\foo")
                // so the path is safe to use on the filesystem. Windows path components cannot
                // contain ':'. The full namespaced path is still passed to CascOpenFile inside
                // ExtractSingleFile because CascLib resolves it reliably.
                string fsRelPath = StripCascNamespace(virtualPath);
                string destPath  = Path.Combine(installPath, fsRelPath);
                string? destDir  = Path.GetDirectoryName(destPath);
                if (destDir != null)
                    Directory.CreateDirectory(destDir);

                ExtractSingleFile(hStorage, virtualPath, destPath, log);

                manifest.ExtractedFiles.Add(fsRelPath);
                bytesProcessed += (long)fileSize;
                processed++;

                progress?.Report(new ExtractionProgress(
                    processed, files.Count, virtualPath, bytesProcessed, totalBytes));

                // Periodically flush the manifest so partial progress is recoverable.
                if (processed % 500 == 0)
                    ManifestService.SaveManifest(installation, manifest);
            }

            manifest.IsComplete = true;
            manifest.TotalBytesExtracted = bytesProcessed;
            ManifestService.SaveManifest(installation, manifest);
            log?.Invoke($"Extraction complete. {processed:N0} files, {FormatBytes(bytesProcessed)} written.");
            return manifest;
        }
        finally
        {
            CascLib.CascCloseStorage(hStorage);
        }
    }

    private static void ExtractSingleFile(IntPtr hStorage, string virtualPath, string destPath, Action<string>? log)
    {
        if (!CascLib.CascOpenFile(hStorage, virtualPath, 0, CascLib.CASC_OPEN_BY_NAME, out IntPtr hFile)
            || hFile == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            log?.Invoke($"  WARN: Could not open '{virtualPath}' (Win32 error {err}). Skipping.");
            return;
        }

        try
        {
            uint sizeHigh;
            uint sizeLow = CascLib.CascGetFileSize(hFile, out sizeHigh);
            if (sizeLow == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0)
            {
                log?.Invoke($"  WARN: Could not get size for '{virtualPath}'. Skipping.");
                return;
            }

            long totalSize = ((long)sizeHigh << 32) | sizeLow;
            if (totalSize == 0)
            {
                // Write empty file so it exists on disk.
                File.WriteAllBytes(destPath, Array.Empty<byte>());
                return;
            }

            const int ChunkSize = 1024 * 1024; // 1 MB chunks
            byte[] buffer = new byte[ChunkSize];

            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 65536, useAsync: false);

            long remaining = totalSize;
            while (remaining > 0)
            {
                uint toRead = (uint)Math.Min(remaining, ChunkSize);
                if (!CascLib.CascReadFile(hFile, buffer, toRead, out uint bytesRead) || bytesRead == 0)
                    break;
                fs.Write(buffer, 0, (int)bytesRead);
                remaining -= bytesRead;
            }
        }
        finally
        {
            CascLib.CascCloseFile(hFile);
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

        // Remove now-empty directories for the three target prefixes.
        // Strip the CASC namespace prefix before building the filesystem path.
        foreach (string prefix in TargetPrefixes)
        {
            string fsPrefix = StripCascNamespace(prefix);
            string dir = Path.Combine(installation.FolderPath, fsPrefix.TrimEnd('\\'));
            RemoveEmptyDirectories(dir, log);
        }

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

        // D2R stores CASC index files under "Data\indices"
        string indicesPath = Path.Combine(folderPath, "Data", "indices");
        if (!Directory.Exists(indicesPath))
            return "The selected folder does not appear to be a D2R installation. " +
                   "Expected a 'Data\\indices' subfolder. " +
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
    /// Example: <c>"data:data\global\allcofs.bin"</c> → <c>"data\global\allcofs.bin"</c>
    /// </summary>
    private static string StripCascNamespace(string cascPath)
    {
        int i = cascPath.IndexOf(':');
        return i >= 0 ? cascPath.Substring(i + 1) : cascPath;
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

