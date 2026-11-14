using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

    private SteamStaticStorage(StaticContainer container, List<Tvfs.FileEntry> allFiles, string keySource)
    {
        _container = container;
        _allFiles = allFiles;
        KeySource = keySource;
    }

    /// <summary>
    /// Which <see cref="Models.KeySource"/> the entries' content keys come from — the text ROOT's
    /// content MD5 when it verifies, otherwise the TVFS encoding keys.
    /// </summary>
    public string KeySource { get; }

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

        // The Steam TVFS omits path separators for some entries (e.g. it yields
        // "…\monster\baalcoldtrail.flac" instead of "…\monster\baal\coldtrail.flac"),
        // which would write those files to the wrong location and break the game at
        // launch. The correct paths live in the "index" text ROOT; join them onto the
        // TVFS encoding keys to recover the real paths.
        files = ApplyTextRootPaths(container, config, files, log);

        string keySource = DetermineKeySource(container, files, log);

        return new SteamStaticStorage(container, files, keySource);
    }

    /// <summary>
    /// Rewrites file paths using the "index" text ROOT so path separators (and casing)
    /// exactly match the canonical layout — the same paths Battle.net/CascLib produce.
    ///
    /// The text ROOT is a newline-delimited list of <c>fullpath|md5|plugin|</c> records
    /// (verified against the build config's <c>root</c> CKey). Each canonical path is
    /// matched to a TVFS entry — which carries the encoding key needed to read the file —
    /// via a separator-stripped, lowercased lookup key. If the ROOT is missing or does
    /// not verify, the raw TVFS paths are used unchanged (best effort, no regression).
    /// </summary>
    private static List<Tvfs.FileEntry> ApplyTextRootPaths(
        StaticContainer container, SteamBuildConfig config, List<Tvfs.FileEntry> files, Action<string>? log)
    {
        Tvfs.FileEntry? indexEntry = files.FirstOrDefault(
            f => f.VirtualPath.Equals("index", StringComparison.OrdinalIgnoreCase) && f.Spans.Count > 0);
        if (indexEntry == null)
        {
            log?.Invoke("No 'index' text ROOT present — using raw TVFS paths.");
            return files;
        }

        byte[] indexData;
        try { indexData = container.OpenByEKey(indexEntry.Spans[0].EKey); }
        catch (Exception ex)
        {
            log?.Invoke($"Could not read 'index' text ROOT ({ex.Message}) — using raw TVFS paths.");
            return files;
        }

        // Verify the ROOT is the one declared by the build config.
        if (config.Root.Length == 16 && !MD5.HashData(indexData).AsSpan().SequenceEqual(config.Root))
        {
            log?.Invoke("'index' text ROOT hash does not match build config — using raw TVFS paths.");
            return files;
        }

        // Map separator-stripped lowercase path → TVFS entry (carries the encoding key).
        var map = new Dictionary<string, Tvfs.FileEntry>(files.Count, StringComparer.Ordinal);
        foreach (Tvfs.FileEntry f in files)
            map[SeparatorlessLower(f.VirtualPath)] = f;

        var corrected = new List<Tvfs.FileEntry>(files.Count);
        int matched = 0, missed = 0;
        string text = Encoding.UTF8.GetString(indexData);
        foreach (string rawLine in text.Split('\n'))
        {
            int len = rawLine.Length;
            if (len > 0 && rawLine[len - 1] == '\r') len--;
            if (len == 0) continue;

            int bar = rawLine.IndexOf('|');
            int nameLen = (bar >= 0 && bar < len) ? bar : len;
            if (nameLen <= 0) continue;

            string path = rawLine[..nameLen];
            if (map.TryGetValue(SeparatorlessLower(path), out Tvfs.FileEntry? entry))
            {
                matched++;
                corrected.Add(new Tvfs.FileEntry
                {
                    VirtualPath = path.Replace('/', '\\').ToLowerInvariant(),
                    Size = entry.Size,
                    Spans = entry.Spans,
                    ContentKey = ParseRootMd5(rawLine, bar, len),
                });
            }
            else missed++;
        }

        // Guard against a bad/partial ROOT wiping the file list.
        if (corrected.Count < files.Count / 2)
        {
            log?.Invoke($"Text ROOT join matched only {corrected.Count:N0}/{files.Count:N0} — using raw TVFS paths.");
            return files;
        }

        log?.Invoke($"Applied canonical paths from text ROOT: {matched:N0} files matched" +
                    (missed > 0 ? $", {missed:N0} unmatched." : "."));
        return corrected;
    }

    /// <summary>
    /// Extracts the second <c>|</c>-delimited field of a text-ROOT record — a 32-char hex digest —
    /// as a lower-case hex string, or null if the record does not have one in that shape.
    /// Records look like <c>fullpath|md5|plugin|</c>.
    /// </summary>
    private static string? ParseRootMd5(string rawLine, int firstBar, int len)
    {
        if (firstBar < 0 || firstBar + 1 >= len) return null;

        int start = firstBar + 1;
        int end = rawLine.IndexOf('|', start);
        if (end < 0 || end > len) end = len;
        if (end - start != 32) return null;

        for (int i = start; i < end; i++)
        {
            char c = rawLine[i];
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return null;
        }
        return rawLine[start..end].ToLowerInvariant();
    }

    /// <summary>
    /// Decides whether the text-ROOT digests really are content MD5s before they are trusted as
    /// content keys, by decoding the smallest entry that has one and hashing it.
    ///
    /// This costs one small file read and removes the need to assume anything about the ROOT's
    /// second column: if the check fails the storage falls back to encoding keys, which still
    /// detect change but cannot be compared against a hash of the file on disk.
    /// </summary>
    private static string DetermineKeySource(
        StaticContainer container, List<Tvfs.FileEntry> files, Action<string>? log)
    {
        Tvfs.FileEntry? probe = files
            .Where(f => f.ContentKey != null && f.Size > 0 && f.Size < 4 * 1024 * 1024 && f.Spans.Count == 1)
            .OrderBy(f => f.Size)
            .FirstOrDefault();

        if (probe == null)
        {
            log?.Invoke("Text ROOT supplied no usable content digests — using encoding keys for change detection.");
            return Models.KeySource.SteamEKey;
        }

        try
        {
            byte[] data = container.OpenByEKey(probe.Spans[0].EKey);
            string actual = Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
            if (actual == probe.ContentKey)
            {
                log?.Invoke("Text ROOT digests verified as content MD5s — updates can verify extracted files directly.");
                return Models.KeySource.SteamRootMd5;
            }

            log?.Invoke($"Text ROOT digest for '{probe.VirtualPath}' is not the content MD5 " +
                        "— using encoding keys for change detection instead.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not verify text ROOT digests ({ex.Message}) — using encoding keys for change detection.");
        }
        return Models.KeySource.SteamEKey;
    }

    /// <summary>
    /// Content key for an entry under the active <see cref="KeySource"/>. For the encoding-key
    /// fallback the spans are folded into a single digest so multi-span files still get one stable
    /// identity, and no archive data has to be decoded to compute it.
    /// </summary>
    private string? ContentKeyFor(Tvfs.FileEntry entry)
    {
        if (KeySource == Models.KeySource.SteamRootMd5)
            return entry.ContentKey;

        if (entry.Spans.Count == 0)
            return null;

        if (entry.Spans.Count == 1)
            return Convert.ToHexString(entry.Spans[0].EKey).ToLowerInvariant();

        var joined = new byte[entry.Spans.Count * 16];
        for (int i = 0; i < entry.Spans.Count; i++)
            entry.Spans[i].EKey.CopyTo(joined, i * 16);
        return Convert.ToHexString(MD5.HashData(joined)).ToLowerInvariant();
    }

    /// <summary>Lowercases a path and removes all path separators, for ROOT↔TVFS joining.</summary>
    private static string SeparatorlessLower(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (c != '/' && c != '\\')
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// Enumerates files whose virtual path begins with one of <paramref name="prefixes"/>.
    /// Matching entries are cached for extraction.
    /// </summary>
    public IEnumerable<Models.StorageEntry> EnumerateFiles(
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
                    yield return new Models.StorageEntry(
                        entry.VirtualPath, (ulong)entry.Size, ContentKeyFor(entry));
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
