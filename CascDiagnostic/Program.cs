using System.Runtime.InteropServices;
using System.Text;
using CascDiagnostic.Native;

const string DefaultPath = @"D:\Games\Blizzard\Diablo II Resurrected Public Test";

string installPath = args.Length > 0 ? args[0] : DefaultPath;

// Tee output to both console and a report file.
string reportPath = Path.Combine(AppContext.BaseDirectory, "casc_diagnostic_report.txt");
using var reportFile = new StreamWriter(reportPath, append: false, Encoding.UTF8);
var originalOut = Console.Out;
Console.SetOut(new TeeWriter(originalOut, reportFile));

void Log(string msg) => Console.WriteLine(msg);

Log("=== CASC International Files Diagnostic Tool ===");
Log($"Installation: {installPath}");
Log($"Report file:  {reportPath}");
Log("");

// --- Phase 0: Validate ---
if (!Directory.Exists(installPath))
{
    Log($"ERROR: Folder does not exist: {installPath}");
    return;
}
string indicesPath = Path.Combine(installPath, "Data", "indices");
if (!Directory.Exists(indicesPath))
{
    Log($"ERROR: No Data\\indices subfolder found — not a valid D2R installation.");
    return;
}
Log("Installation folder validated OK.");
Log("");

// --- Phase 1: Open CASC Storage ---
Log("=== PHASE 1: Opening CASC Storage ===");
IntPtr hStorage;
try
{
    hStorage = CascLib.OpenStorageWithFallback(installPath, Log);
}
catch (Exception ex)
{
    Log($"FATAL: {ex.Message}");
    return;
}

long totalFileCount = CascLib.GetTotalFileCount(hStorage);
Log($"Storage opened successfully. Total files reported: {(totalFileCount > 0 ? totalFileCount.ToString("N0") : "unknown")}");
Log("");

// --- Phase 2: Full Prefix Discovery ---
Log("=== PHASE 2: Full Prefix Discovery ===");
Log("Enumerating ALL files in CASC storage (this takes 2-3 minutes)...");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Stats dictionaries
var level1Stats = new Dictionary<string, (long Count, long TotalSize)>(StringComparer.OrdinalIgnoreCase);
var level2Stats = new Dictionary<string, (long Count, long TotalSize)>(StringComparer.OrdinalIgnoreCase);
var level3Stats = new Dictionary<string, (long Count, long TotalSize)>(StringComparer.OrdinalIgnoreCase);
var level2Samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

// Also track locale flags distribution
var localeFlagCounts = new Dictionary<uint, long>();
var contentFlagCounts = new Dictionary<uint, long>();
var availabilityCounts = new Dictionary<uint, long>();

// Store sample files for extractability testing (non-standard prefixes)
var knownPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    @"data:data\global\", @"data:data\hd\", @"data:data\local\"
};
var testCandidates = new Dictionary<string, List<(string Path, ulong Size)>>(StringComparer.OrdinalIgnoreCase);

long enumerated = 0;
var enumSw = System.Diagnostics.Stopwatch.StartNew();

foreach (var (virtualPath, fileSize, localeFlags, contentFlags, available) in
    CascLib.EnumerateFilesEx(hStorage, new[] { "" }, cts.Token,
        onScanProgress: f => Console.Error.Write($"\r  Scanning: {enumerated:N0} files..."),
        onIndexBuildComplete: ms => Log($"  File index opened in {ms / 1000.0:F1}s — scanning entries..."),
        onDiagnosticLog: Log))
{
    enumerated++;

    // Compute prefix keys
    string l1 = GetPrefixAtDepth(virtualPath, 1);
    string l2 = GetPrefixAtDepth(virtualPath, 2);
    string l3 = GetPrefixAtDepth(virtualPath, 3);

    Increment(level1Stats, l1, fileSize);
    Increment(level2Stats, l2, fileSize);
    Increment(level3Stats, l3, fileSize);

    // Collect samples per L2 prefix
    if (!level2Samples.TryGetValue(l2, out var samples))
    {
        samples = new List<string>();
        level2Samples[l2] = samples;
    }
    if (samples.Count < 5)
        samples.Add(virtualPath);

    // Track flags
    IncrementDict(localeFlagCounts, localeFlags);
    IncrementDict(contentFlagCounts, contentFlags);
    IncrementDict(availabilityCounts, available);

    // Collect test candidates for non-standard prefixes
    if (!knownPrefixes.Any(kp => virtualPath.StartsWith(kp, StringComparison.OrdinalIgnoreCase)))
    {
        if (!testCandidates.TryGetValue(l2, out var candidates))
        {
            candidates = new List<(string, ulong)>();
            testCandidates[l2] = candidates;
        }
        if (candidates.Count < 3)
            candidates.Add((virtualPath, fileSize));
    }

    if (enumerated % 100_000 == 0)
        Console.Error.Write($"\r  Scanning: {enumerated:N0} files...");
}

enumSw.Stop();
Console.Error.WriteLine(); // clear progress line
Log($"Enumeration complete: {enumerated:N0} files in {enumSw.Elapsed.TotalSeconds:F1}s");
Log("");

// Print Level 1
Log("--- Level 1 Prefixes ---");
foreach (var kv in level1Stats.OrderByDescending(x => x.Value.Count))
    Log($"  {kv.Key,-30} — {kv.Value.Count,10:N0} files, {FormatBytes(kv.Value.TotalSize),10}");
Log("");

// Print Level 2
Log("--- Level 2 Prefixes ---");
foreach (var kv in level2Stats.OrderByDescending(x => x.Value.Count))
    Log($"  {kv.Key,-45} — {kv.Value.Count,10:N0} files, {FormatBytes(kv.Value.TotalSize),10}");
Log("");

// Print Level 3 (locale-related only)
Log("--- Level 3 Prefixes (locale-related) ---");
var localeKeywords = new[] { "locale", "speech", "audio", "lang", "dub",
    "deDE", "enUS", "esES", "esMX", "frFR", "itIT", "jaJP", "koKR", "plPL", "ptBR", "ruRU", "zhTW" };
foreach (var kv in level3Stats
    .Where(x => localeKeywords.Any(lk => x.Key.Contains(lk, StringComparison.OrdinalIgnoreCase)))
    .OrderByDescending(x => x.Value.Count))
{
    Log($"  {kv.Key,-55} — {kv.Value.Count,10:N0} files, {FormatBytes(kv.Value.TotalSize),10}");
}
Log("");

// Print sample paths for non-standard prefixes
Log("--- Sample Paths (non-standard prefixes) ---");
foreach (var kv in level2Samples
    .Where(x => !knownPrefixes.Any(kp => x.Key.StartsWith(kp, StringComparison.OrdinalIgnoreCase)))
    .OrderBy(x => x.Key))
{
    Log($"  [{kv.Key}]");
    foreach (var sample in kv.Value)
        Log($"    {sample}");
}
Log("");

// --- Phase 3: Locale Flag Analysis ---
Log("=== PHASE 3: Locale & Content Flag Distribution ===");
Log("--- Locale Flags ---");
foreach (var kv in localeFlagCounts.OrderByDescending(x => x.Value))
    Log($"  0x{kv.Key:X8} — {kv.Value:N0} files");
Log("");
Log("--- Content Flags ---");
foreach (var kv in contentFlagCounts.OrderByDescending(x => x.Value))
    Log($"  0x{kv.Key:X8} — {kv.Value:N0} files");
Log("");
Log("--- File Availability ---");
foreach (var kv in availabilityCounts.OrderByDescending(x => x.Value))
    Log($"  bFileAvailable={kv.Key} — {kv.Value:N0} files");
Log("");

// --- Phase 4: Extractability Test ---
Log("=== PHASE 4: Extractability Tests ===");

// Need a fresh storage handle for file operations
CascLib.CascCloseStorage(hStorage);
hStorage = CascLib.OpenStorageWithFallback(installPath, null);

byte[] readBuffer = new byte[64];

foreach (var kv in testCandidates.OrderBy(x => x.Key))
{
    Log($"  [{kv.Key}]");
    foreach (var (path, size) in kv.Value)
    {
        if (!CascLib.CascOpenFile(hStorage, path, 0, CascLib.CASC_OPEN_BY_NAME, out IntPtr hFile) || hFile == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Log($"    [FAIL] {path} — CascOpenFile error {err}");
            continue;
        }

        uint sizeHigh;
        uint sizeLow = CascLib.CascGetFileSize(hFile, out sizeHigh);
        long reportedSize = ((long)sizeHigh << 32) | sizeLow;

        bool readOk = CascLib.CascReadFile(hFile, readBuffer, 64, out uint bytesRead);
        CascLib.CascCloseFile(hFile);

        string hexDump = bytesRead > 0
            ? BitConverter.ToString(readBuffer, 0, (int)Math.Min(bytesRead, 16)).Replace("-", " ")
            : "(no data)";

        string status = readOk && bytesRead > 0 ? "OK" : "READ_FAIL";
        Log($"    [{status,-9}] {path}");
        Log($"              Size: {reportedSize:N0} bytes, Read: {bytesRead} bytes");
        Log($"              Header: {hexDump}");
    }
}
Log("");

CascLib.CascCloseStorage(hStorage);

// --- Phase 5: Comparison Report ---
Log("=== PHASE 5: Comparison & Recommendation ===");
Log($"Current D2RExtractor InternationalPrefixes: {{ @\"data:locales\\\" }}");
Log("");

// Check if data:locales\ was found
bool localesPrefixFound = level2Stats.Keys.Any(k => k.StartsWith(@"data:locales\", StringComparison.OrdinalIgnoreCase));
long localesFileCount = level1Stats.Where(k => k.Key.StartsWith(@"data:locales", StringComparison.OrdinalIgnoreCase)).Sum(k => k.Value.Count);
long localesSize = level1Stats.Where(k => k.Key.StartsWith(@"data:locales", StringComparison.OrdinalIgnoreCase)).Sum(k => k.Value.TotalSize);

if (localesPrefixFound && localesFileCount > 0)
{
    Log($"FOUND: {localesFileCount:N0} files ({FormatBytes(localesSize)}) match the 'data:locales\\' prefix.");
    Log("Recommendation: The current InternationalPrefixes appears CORRECT.");
}
else
{
    Log("NOT FOUND: No files matched the 'data:locales\\' prefix.");
    Log("Investigate the non-standard prefixes above for international content.");

    // Look for any prefix containing locale keywords
    var candidates = level2Stats.Keys
        .Where(k => !knownPrefixes.Any(kp => k.StartsWith(kp, StringComparison.OrdinalIgnoreCase)))
        .Where(k => localeKeywords.Any(lk => k.Contains(lk, StringComparison.OrdinalIgnoreCase)))
        .ToList();
    if (candidates.Count > 0)
    {
        Log("Possible alternative prefixes:");
        foreach (var c in candidates)
            Log($"  {c} — {level2Stats[c].Count:N0} files, {FormatBytes(level2Stats[c].TotalSize)}");
    }
}

// Also report ALL non-standard prefixes as potential international content
var allNonStandard = level2Stats
    .Where(x => !knownPrefixes.Any(kp => x.Key.StartsWith(kp, StringComparison.OrdinalIgnoreCase)))
    .OrderByDescending(x => x.Value.TotalSize)
    .ToList();
if (allNonStandard.Count > 0)
{
    Log("");
    Log("All non-standard Level-2 prefixes found:");
    foreach (var kv in allNonStandard)
        Log($"  {kv.Key,-45} — {kv.Value.Count,10:N0} files, {FormatBytes(kv.Value.TotalSize),10}");
}

Log("");
Log($"=== Diagnostic complete. Report saved to: {reportPath} ===");

// Flush the tee writer
Console.Out.Flush();
Console.SetOut(originalOut);

return;

// -----------------------------------------------------------------------
// Helper functions
// -----------------------------------------------------------------------

static string GetPrefixAtDepth(string virtualPath, int depth)
{
    // For "data:data\global\ui\file.bin":
    //   depth 1 → "data:data\"
    //   depth 2 → "data:data\global\"
    //   depth 3 → "data:data\global\ui\"
    //
    // The colon-prefixed namespace counts as part of the first segment.
    // So we find the colon first, then count backslash separators from there.

    int colonIdx = virtualPath.IndexOf(':');
    int searchFrom = colonIdx >= 0 ? colonIdx + 1 : 0;

    int pos = searchFrom;
    for (int d = 0; d < depth && pos < virtualPath.Length; d++)
    {
        int next = virtualPath.IndexOf('\\', pos);
        if (next < 0)
            return virtualPath; // entire path is the prefix
        pos = next + 1;
    }

    return virtualPath[..pos];
}

static void Increment(Dictionary<string, (long Count, long TotalSize)> dict, string key, ulong fileSize)
{
    if (dict.TryGetValue(key, out var val))
        dict[key] = (val.Count + 1, val.TotalSize + (long)fileSize);
    else
        dict[key] = (1, (long)fileSize);
}

static void IncrementDict(Dictionary<uint, long> dict, uint key)
{
    dict[key] = dict.TryGetValue(key, out long v) ? v + 1 : 1;
}

static string FormatBytes(long bytes)
{
    if (bytes >= 1024L * 1024 * 1024)
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    if (bytes >= 1024 * 1024)
        return $"{bytes / (1024.0 * 1024):F1} MB";
    if (bytes >= 1024)
        return $"{bytes / 1024.0:F1} KB";
    return $"{bytes} B";
}

/// <summary>
/// TextWriter that writes to two underlying writers simultaneously.
/// </summary>
class TeeWriter : TextWriter
{
    private readonly TextWriter _a;
    private readonly TextWriter _b;

    public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }

    public override Encoding Encoding => _a.Encoding;

    public override void Write(char value) { _a.Write(value); _b.Write(value); }
    public override void Write(string? value) { _a.Write(value); _b.Write(value); }
    public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
    public override void Flush() { _a.Flush(); _b.Flush(); }
}
