using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using D2RExtractor.Models;
using Newtonsoft.Json;

namespace D2RExtractor.Services;

/// <summary>
/// Reads and writes the per-installation extraction manifest and the global app settings.
/// </summary>
public static class ManifestService
{
    // -----------------------------------------------------------------------
    // App settings (list of managed installations)
    // -----------------------------------------------------------------------

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "D2RExtractor");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly string PreferencesPath = Path.Combine(SettingsDir, "preferences.json");

    /// <summary>Loads the saved list of D2R installations. Returns an empty list if none saved yet.</summary>
    public static List<D2RInstallation> LoadInstallations()
    {
        if (!File.Exists(SettingsPath))
            return new List<D2RInstallation>();

        try
        {
            string json = File.ReadAllText(SettingsPath);
            return JsonConvert.DeserializeObject<List<D2RInstallation>>(json)
                   ?? new List<D2RInstallation>();
        }
        catch
        {
            return new List<D2RInstallation>();
        }
    }

    /// <summary>Persists the current list of D2R installations to disk.</summary>
    public static void SaveInstallations(IEnumerable<D2RInstallation> installations)
    {
        Directory.CreateDirectory(SettingsDir);
        string json = JsonConvert.SerializeObject(installations, Formatting.Indented);
        File.WriteAllText(SettingsPath, json);
    }

    /// <summary>Loads user preferences. Returns defaults if the file doesn't exist or is corrupt.</summary>
    public static AppPreferences LoadPreferences()
    {
        if (!File.Exists(PreferencesPath))
            return new AppPreferences();

        try
        {
            string json = File.ReadAllText(PreferencesPath);
            return JsonConvert.DeserializeObject<AppPreferences>(json) ?? new AppPreferences();
        }
        catch
        {
            return new AppPreferences(); // caller should log a warning
        }
    }

    /// <summary>Saves user preferences to disk.</summary>
    public static void SavePreferences(AppPreferences prefs)
    {
        Directory.CreateDirectory(SettingsDir);
        string json = JsonConvert.SerializeObject(prefs, Formatting.Indented);
        File.WriteAllText(PreferencesPath, json);
    }

    // -----------------------------------------------------------------------
    // Per-destination extraction manifest
    // -----------------------------------------------------------------------

    /// <summary>
    /// Loads the extraction manifest for the given destination.
    /// Returns null if no manifest exists (not yet extracted).
    /// </summary>
    public static ExtractionManifest? LoadManifest(ExtractionTarget target)
    {
        if (!File.Exists(target.ManifestPath))
            return null;

        try
        {
            string json = File.ReadAllText(target.ManifestPath);
            return JsonConvert.DeserializeObject<ExtractionManifest>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Saves the extraction manifest header for the given destination.
    ///
    /// <para>
    /// Written to a temp file and moved into place. A plain truncate-and-write can be interrupted
    /// (crash, power loss) and leave invalid JSON, which makes <see cref="LoadManifest"/> return
    /// null — and an installation with no readable manifest reports as neither extracted nor
    /// partially extracted, which disables Undo and strands every extracted file. The move is
    /// atomic on the same volume, so the manifest is always either the old one or the new one.
    /// </para>
    /// </summary>
    public static void SaveManifest(ExtractionTarget target, ExtractionManifest manifest)
    {
        string path = target.ManifestPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(manifest, Formatting.Indented));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Deletes the extraction manifest and its file-list sidecar (called during Undo).
    /// Both must go: a stale sidecar left behind would keep the <c>data\</c> folder non-empty
    /// and misrepresent a fresh install as having been extracted.
    /// </summary>
    public static void DeleteManifest(ExtractionTarget target)
    {
        foreach (string path in new[]
                 {
                     target.ManifestPath,
                     target.ManifestPath + ".tmp",
                     GetEntryFilePath(target, ExtractionManifest.DefaultEntryFile),
                     GetEntryFilePath(target, ExtractionManifest.DefaultEntryFile) + ".new",
                 })
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Per-destination file list (the manifest's sidecar)
    //
    // Format: one record per line, "<relPath>\t<contentKeyHex>\t<size>", UTF-8 without a BOM,
    // LF-terminated. Tabs cannot occur in a Windows path, so no escaping is needed. The key may
    // be empty for entries whose source could not supply one.
    // -----------------------------------------------------------------------

    /// <summary>Absolute path of the sidecar that holds <paramref name="entryFile"/> for this install.</summary>
    public static string GetEntryFilePath(ExtractionTarget target, string? entryFile)
    {
        string dir = Path.GetDirectoryName(target.ManifestPath)!;
        return Path.Combine(dir, string.IsNullOrWhiteSpace(entryFile)
            ? ExtractionManifest.DefaultEntryFile
            : entryFile);
    }

    /// <summary>Absolute path of the sidecar described by <paramref name="manifest"/>.</summary>
    public static string GetEntryFilePath(ExtractionTarget target, ExtractionManifest manifest) =>
        GetEntryFilePath(target, manifest.EntryFile);

    /// <summary>
    /// Streams every file recorded in <paramref name="manifest"/>, presenting both schema versions
    /// uniformly: legacy (v1.1.6 and earlier) manifests yield their path list with no key and an
    /// unknown size, current manifests stream the sidecar.
    ///
    /// <para>
    /// Streaming matters — a full extraction records roughly 150,000 files, and both Undo and
    /// Update walk the whole list.
    /// </para>
    /// </summary>
    public static IEnumerable<ManifestEntry> EnumerateEntries(
        ExtractionTarget target, ExtractionManifest manifest)
    {
        if (manifest.IsLegacySchema)
        {
            foreach (string relPath in manifest.ExtractedFiles ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(relPath))
                    yield return new ManifestEntry(relPath, null, -1);
            }
            yield break;
        }

        string path = GetEntryFilePath(target, manifest);
        if (!File.Exists(path))
            yield break;

        foreach (string line in File.ReadLines(path))
        {
            if (TryParseEntry(line, out ManifestEntry entry))
                yield return entry;
        }
    }

    /// <summary>
    /// Parses one sidecar record. Returns false for blank lines and for a torn final line, which
    /// is the worst a crash mid-append can produce.
    /// </summary>
    private static bool TryParseEntry(string line, out ManifestEntry entry)
    {
        entry = default;
        if (string.IsNullOrEmpty(line)) return false;

        int t1 = line.IndexOf('\t');
        if (t1 <= 0) return false;

        int t2 = line.IndexOf('\t', t1 + 1);
        if (t2 < 0) return false;

        string relPath = line[..t1];
        string key = line[(t1 + 1)..t2];
        if (!long.TryParse(line[(t2 + 1)..], out long size)) return false;

        entry = new ManifestEntry(relPath, key.Length == 0 ? null : key, size);
        return true;
    }

    /// <summary>Formats one sidecar record, without its trailing newline.</summary>
    private static string FormatEntry(in ManifestEntry entry) =>
        $"{entry.RelPath}\t{entry.Key}\t{entry.Size}";

    /// <summary>
    /// Replaces the sidecar wholesale, via a temp file and an atomic move. Used at the end of an
    /// update, where entries have been removed or re-keyed and appending is no longer enough.
    /// </summary>
    public static void WriteAllEntries(
        ExtractionTarget target, ExtractionManifest manifest, IEnumerable<ManifestEntry> entries)
    {
        string path = GetEntryFilePath(target, manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string tmp = path + ".new";
        int count = 0;
        using (var writer = CreateEntryWriter(tmp, append: false))
        {
            foreach (ManifestEntry entry in entries)
            {
                writer.Write(FormatEntry(entry));
                writer.Write('\n');
                count++;
            }
        }
        File.Move(tmp, path, overwrite: true);
        manifest.EntryCount = count;
    }

    /// <summary>Removes the sidecar so a fresh extraction does not append to a previous run's list.</summary>
    public static void ResetEntries(ExtractionTarget target, ExtractionManifest manifest)
    {
        string path = GetEntryFilePath(target, manifest);
        if (File.Exists(path))
            File.Delete(path);
        manifest.EntryCount = 0;
    }

    private static StreamWriter CreateEntryWriter(string path, bool append) =>
        new(path, append, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024)
        {
            AutoFlush = false,
        };

    /// <summary>
    /// Append-only writer for the sidecar, used during extraction and update.
    ///
    /// <para>
    /// Appending rather than rewriting is what keeps the manifest's own disk writes proportional
    /// to the number of files instead of to their square: the previous scheme re-serialised the
    /// entire growing list every 500 files, which cost several gigabytes of writes over a full
    /// extraction. Flushing is time-based so the cost does not scale with file count either.
    /// </para>
    /// </summary>
    public sealed class EntryWriter : IDisposable
    {
        private const int FlushIntervalMs = 5000;

        private readonly StreamWriter _writer;
        private readonly System.Diagnostics.Stopwatch _sinceFlush = System.Diagnostics.Stopwatch.StartNew();

        internal EntryWriter(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _writer = CreateEntryWriter(path, append: true);
        }

        /// <summary>Number of records appended by this writer.</summary>
        public int Appended { get; private set; }

        public void Append(in ManifestEntry entry)
        {
            _writer.Write(FormatEntry(entry));
            _writer.Write('\n');
            Appended++;

            if (_sinceFlush.ElapsedMilliseconds >= FlushIntervalMs)
            {
                _writer.Flush();
                _sinceFlush.Restart();
            }
        }

        /// <summary>Pushes buffered records to disk. Call before reporting progress the user could act on.</summary>
        public void Flush()
        {
            _writer.Flush();
            _sinceFlush.Restart();
        }

        public void Dispose() => _writer.Dispose();
    }

    /// <summary>Opens an <see cref="EntryWriter"/> that appends to this manifest's sidecar.</summary>
    public static EntryWriter OpenEntryWriter(ExtractionTarget target, ExtractionManifest manifest) =>
        new(GetEntryFilePath(target, manifest));

    // -----------------------------------------------------------------------
    // Last run's changes
    //
    // Two files beside the manifest: ".extraction_run.json" (the summary) and
    // ".extraction_changes.txt" (one record per changed file, "<A|U|R>\t<relPath>\t<size>").
    // Only the most recent run is kept — the previous one is overwritten, not accumulated.
    // -----------------------------------------------------------------------

    private const string RunRecordFile = ".extraction_run.json";

    private static string RunRecordPath(ExtractionTarget target) =>
        Path.Combine(Path.GetDirectoryName(target.ManifestPath)!, RunRecordFile);

    private static string ChangeListPath(ExtractionTarget target) =>
        Path.Combine(Path.GetDirectoryName(target.ManifestPath)!, ExtractionRunRecord.DefaultChangeFile);

    /// <summary>The last run's summary for this destination, or null if it has never run.</summary>
    public static ExtractionRunRecord? LoadRunRecord(ExtractionTarget target)
    {
        string path = RunRecordPath(target);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonConvert.DeserializeObject<ExtractionRunRecord>(File.ReadAllText(path));
        }
        catch
        {
            // A record nobody can read is the same as no record: this is a history note, and
            // failing an update over it would be absurd.
            return null;
        }
    }

    /// <summary>
    /// Writes the last run's summary, atomically, for the same reason the manifest is written that
    /// way — a torn file here reads as "never ran" and silently loses the history.
    /// </summary>
    public static void SaveRunRecord(ExtractionTarget target, ExtractionRunRecord record)
    {
        string path = RunRecordPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(record, Formatting.Indented));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Replaces the change list for this destination. Written once at the end of a run, when the
    /// full set is known.
    /// </summary>
    public static void WriteChanges(ExtractionTarget target, IEnumerable<ExtractionChange> changes)
    {
        string path = ChangeListPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string tmp = path + ".new";
        using (var writer = CreateEntryWriter(tmp, append: false))
        {
            foreach (ExtractionChange change in changes)
            {
                writer.Write(change.Kind switch
                {
                    ChangeKind.Added => 'A',
                    ChangeKind.Updated => 'U',
                    _ => 'R',
                });
                writer.Write('\t');
                writer.Write(change.RelPath);
                writer.Write('\t');
                writer.Write(change.Size);
                writer.Write('\n');
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Streams the last run's change list. Empty when there is none.</summary>
    public static IEnumerable<ExtractionChange> EnumerateChanges(ExtractionTarget target)
    {
        string path = ChangeListPath(target);
        if (!File.Exists(path)) yield break;

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length < 4) continue;

            int t1 = line.IndexOf('\t');
            if (t1 != 1) continue;

            int t2 = line.IndexOf('\t', t1 + 1);
            if (t2 < 0) continue;

            ChangeKind kind = line[0] switch
            {
                'A' => ChangeKind.Added,
                'U' => ChangeKind.Updated,
                'R' => ChangeKind.Removed,
                _ => (ChangeKind)(-1),
            };
            if ((int)kind < 0) continue;

            long.TryParse(line[(t2 + 1)..], out long size);
            yield return new ExtractionChange(kind, line[(t1 + 1)..t2], size);
        }
    }

    /// <summary>Removes the run history for this destination, as part of Undo.</summary>
    public static void DeleteRunRecord(ExtractionTarget target)
    {
        foreach (string path in new[]
                 {
                     RunRecordPath(target),
                     RunRecordPath(target) + ".tmp",
                     ChangeListPath(target),
                     ChangeListPath(target) + ".new",
                 })
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// Every file this destination's bookkeeping owns. The extraction scan skips these: they live
    /// inside the folder being scanned but are not extracted content, and counting them as such
    /// would have an update try to "remove" them on the next run.
    /// </summary>
    public static IEnumerable<string> BookkeepingPaths(ExtractionTarget target, ExtractionManifest manifest)
    {
        yield return target.ManifestPath;
        yield return GetEntryFilePath(target, manifest);
        yield return RunRecordPath(target);
        yield return ChangeListPath(target);
    }
}
