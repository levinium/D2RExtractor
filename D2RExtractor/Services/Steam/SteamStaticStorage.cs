using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace D2RExtractor.Services.Steam;

/// <summary>
/// A fully-local reader for the Steam D2R "static container" storage
/// (patch build 93236+, mid-2026). It replaces CascLib for Steam installs,
/// which switched from the classic CASC layout (<c>.build.info</c> + <c>*.idx</c>
/// index files) to a self-contained format: a <c>data\.build.config</c> plus flat
/// <c>NN-NNNNNNNN.data</c> archives whose file locations are encoded directly in
/// each file's encoding key.
///
/// Because the format is entirely local, extraction needs no internet connection
/// (unlike the previous CDN-download workaround for Steam patch 3.1.2).
///
/// Provides the same two operations the extractor needs from CascLib:
/// enumerate matching virtual paths, and extract one file by virtual path.
/// </summary>
internal sealed class SteamStaticStorage : IDisposable
{
    private readonly StaticContainer _container;
    private readonly List<Tvfs.FileEntry> _allFiles;
    // Populated during enumeration; maps a selected virtual path → its file entry.
    private readonly Dictionary<string, Tvfs.FileEntry> _selected =
        new(StringComparer.OrdinalIgnoreCase);

    private SteamStaticStorage(StaticContainer container, List<Tvfs.FileEntry> allFiles)
    {
        _container = container;
        _allFiles = allFiles;
    }

    /// <summary>
    /// Returns the path to the <c>.build.config</c> for a Steam static-container
    /// install, or <c>null</c> if this install is not that format.
    /// </summary>
    public static string? FindBuildConfig(string installPath)
    {
        // Steam uses a lowercase "data" folder; probe common casings just in case.
        foreach (string sub in new[] { "data", "Data" })
        {
            string candidate = Path.Combine(installPath, sub, ".build.config");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>True if <paramref name="installPath"/> is a Steam static-container D2R install.</summary>
    public static bool IsSteamStaticStorage(string installPath) => FindBuildConfig(installPath) != null;

    /// <summary>
    /// Opens the storage: parses <c>.build.config</c>, builds the container, and
    /// walks the full TVFS tree. The tree walk is the slow part (a few seconds),
    /// replacing CascLib's multi-minute index build.
    /// </summary>
    public static SteamStaticStorage Open(string installPath, Action<string>? log)
    {
        string? buildConfigPath = FindBuildConfig(installPath)
            ?? throw new InvalidOperationException(
                "No Steam static-container '.build.config' found. This does not look like a Steam D2R install.");

        string dataDir = Path.GetDirectoryName(buildConfigPath)!;
        log?.Invoke($"Steam static-container detected. Reading build config: {buildConfigPath}");

        SteamBuildConfig config = SteamBuildConfig.Load(buildConfigPath);
        log?.Invoke($"  vfs-root EKey present; {config.VfsSubDirectoryEKeys.Count} sub-directory VFS blob(s); " +
                    $"key-layout-index-bits={config.KeyLayoutIndexBits}, {config.KeyLayouts.Count} layout(s).");

        var container = StaticContainer.FromConfig(dataDir, config);

        var sw = Stopwatch.StartNew();
        log?.Invoke("Parsing TVFS file tree (no internet required)…");
        List<Tvfs.FileEntry> files = Tvfs.Parse(container, config.VfsRootEKey, config.VfsSubDirectoryEKeys);
        sw.Stop();
        log?.Invoke($"TVFS tree parsed in {sw.ElapsedMilliseconds / 1000.0:F1}s — {files.Count:N0} total file(s) found.");

        return new SteamStaticStorage(container, files);
    }

    /// <summary>
    /// Enumerates files whose virtual path begins with one of <paramref name="prefixes"/>,
    /// yielding <c>(virtualPath, fileSize)</c>. Matching entries are cached for extraction.
    /// </summary>
    public IEnumerable<(string VirtualPath, ulong FileSize)> EnumerateFiles(
        string[] prefixes,
        CancellationToken ct,
        Action<string>? onScanProgress = null,
        Action<long>? onIndexBuildComplete = null,
        Action<string>? onDiagnosticLog = null)
    {
        // The tree is already parsed in Open(); report "index build" as effectively instant.
        onIndexBuildComplete?.Invoke(0);
        onDiagnosticLog?.Invoke($"Filtering {_allFiles.Count:N0} entries against {prefixes.Length} target prefix(es)…");

        var sw = Stopwatch.StartNew();
        long scanned = 0;
        foreach (Tvfs.FileEntry entry in _allFiles)
        {
            if (ct.IsCancellationRequested) yield break;
            scanned++;

            if (onScanProgress != null && sw.ElapsedMilliseconds >= 500)
            {
                onScanProgress(entry.VirtualPath);
                sw.Restart();
            }

            foreach (string prefix in prefixes)
            {
                if (entry.VirtualPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    _selected[entry.VirtualPath] = entry;
                    yield return (entry.VirtualPath, (ulong)entry.Size);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Decodes the file at <paramref name="virtualPath"/> and writes it to
    /// <paramref name="destPath"/>. Returns false if the file is not available.
    /// </summary>
    public bool ExtractFile(string virtualPath, string destPath)
    {
        if (!_selected.TryGetValue(virtualPath, out Tvfs.FileEntry? entry))
            return false;

        try
        {
            if (entry.Spans.Count == 1)
            {
                // Fast path: the single span's decoded blob is the whole file.
                byte[] data = _container.OpenByEKey(entry.Spans[0].EKey);
                File.WriteAllBytes(destPath, data);
                return true;
            }

            // Multi-span file: place each span's decoded bytes at its logical offset.
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 65536, useAsync: false);
            fs.SetLength(entry.Size);
            foreach (Tvfs.Span span in entry.Spans)
            {
                byte[] data = _container.OpenByEKey(span.EKey);
                int len = (int)Math.Min(span.ContentSize, data.Length);
                fs.Seek(span.ContentOffset, SeekOrigin.Begin);
                fs.Write(data, 0, len);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose() => _container.Dispose();
}
