using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace D2RExtractor.Native;

/// <summary>
/// P/Invoke declarations for Ladislav Zezula's CascLib.dll.
///
/// IMPORTANT: You must place CascLib.dll (x64) in the Tools\ folder before building.
/// Obtain it from either:
///   - Ladik's CASC Viewer download: https://www.zezula.net/en/casc/main.html
///     (extract CascLib.dll from the CascViewer zip)
///   - CascLib GitHub releases: https://github.com/ladislav-zezula/CascLib
///
/// This wrapper targets modern CascLib builds (2.x+) using CASC_MAX_PATH = 1024.
/// If you are using an older build, adjust CASC_MAX_PATH below and recompile.
/// </summary>
internal static class CascLib
{
    private const string DllName = "CascLib.dll";

    /// <summary>
    /// Maximum path length used in CASC_FIND_DATA.szFileName.
    /// Current CascLib (3.x) uses the standard Windows MAX_PATH (260).
    /// Older builds (pre-3.x) used a custom CASC_MAX_PATH of 1024 — if you see garbled
    /// file names with an old DLL, switch this back to 1024.
    /// </summary>
    internal const int CASC_MAX_PATH = 260; // Windows MAX_PATH

    /// <summary>Invalid file data ID sentinel value.</summary>
    internal const uint CASC_INVALID_ID = 0xFFFFFFFF;

    // CascOpenFile flags
    internal const uint CASC_OPEN_BY_NAME = 0x00000000;
    internal const uint CASC_OPEN_BY_DATAFILE_NUMBER = 0x00000001;
    internal const uint CASC_OPEN_BY_CKEY = 0x00000002;
    internal const uint CASC_OPEN_BY_EKEY = 0x00000003;

    /// <summary>
    /// Data returned by CascFindFirstFile / CascFindNextFile.
    /// Layout matches CASC_FIND_DATA in CascLib.h (3.x) for x64:
    ///
    ///   Offset   Size  Field
    ///      0      260  szFileName  (char[MAX_PATH])
    ///    260       16  CKey        (BYTE[MD5_HASH_SIZE]) — unmapped, for offset only
    ///    276       16  EKey        (BYTE[MD5_HASH_SIZE]) — unmapped, for offset only
    ///    292        4  padding     (MSVC aligns ULONGLONG to 8 bytes: 292→296)
    ///    296        8  TagBitMask  (ULONGLONG)
    ///    304        8  FileSize    (ULONGLONG)
    ///    312        8  szPlainName (char*)
    ///    320        4  dwFileDataId
    ///    324        4  dwLocaleFlags
    ///    328        4  dwContentFlags
    ///    332        4  dwSpanCount
    ///    336        4  bFileAvailable (DWORD bit-field; non-zero = available)
    ///    340        4  NameType    (CASC_NAME_TYPE enum)
    ///   Total: 344 bytes
    /// </summary>
    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Ansi)]
    internal struct CASC_FIND_DATA
    {
        /// <summary>Full virtual path of the file (e.g. "data\global\ui\..."). Null-terminated.</summary>
        [FieldOffset(0)]
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CASC_MAX_PATH)]
        public string szFileName;

        // CKey[16] at offset 260 and EKey[16] at offset 276 are not mapped —
        // we only need szFileName and the fields below.

        [FieldOffset(296)]
        public ulong TagBitMask;

        [FieldOffset(304)]
        public ulong FileSize;

        /// <summary>Pointer into szFileName at the start of the plain file name.</summary>
        [FieldOffset(312)]
        public IntPtr szPlainName;

        [FieldOffset(320)]
        public uint dwFileDataId;

        [FieldOffset(324)]
        public uint dwLocaleFlags;

        [FieldOffset(328)]
        public uint dwContentFlags;

        [FieldOffset(332)]
        public uint dwSpanCount;

        /// <summary>Non-zero when the file is locally available in the CASC storage.</summary>
        [FieldOffset(336)]
        public uint bFileAvailable; // DWORD bit-field in native code — check != 0

        [FieldOffset(340)]
        public uint NameType;
    }

    /// <summary>Opens a CASC storage at the given path.</summary>
    /// <param name="szDataPath">Full path to the game folder (e.g. "C:\Program Files (x86)\Diablo II Resurrected").</param>
    /// <param name="dwLocaleMask">Locale mask; pass 0 for all locales.</param>
    /// <param name="phStorage">Receives the storage handle on success.</param>
    /// <returns>True on success.</returns>
    [DllImport(DllName, EntryPoint = "CascOpenStorage", CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascOpenStorage(string szDataPath, uint dwLocaleMask, out IntPtr phStorage);

    /// <summary>Closes a CASC storage handle.</summary>
    [DllImport(DllName, EntryPoint = "CascCloseStorage",
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascCloseStorage(IntPtr hStorage);

    /// <summary>Begins enumeration of files in the CASC storage.</summary>
    /// <param name="hStorage">Storage handle from CascOpenStorage.</param>
    /// <param name="szMask">Wildcard mask (e.g. "*" for all files).</param>
    /// <param name="pFindData">Receives info about the first matching file.</param>
    /// <param name="szListFile">Path to a list file, or null to use the internal list.</param>
    /// <returns>Find handle, or IntPtr.Zero on failure.</returns>
    [DllImport(DllName, EntryPoint = "CascFindFirstFile", CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    internal static extern IntPtr CascFindFirstFile(IntPtr hStorage, string szMask,
        out CASC_FIND_DATA pFindData, string? szListFile);

    /// <summary>Continues enumeration started by CascFindFirstFile.</summary>
    [DllImport(DllName, EntryPoint = "CascFindNextFile",
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascFindNextFile(IntPtr hFind, out CASC_FIND_DATA pFindData);

    /// <summary>Closes a find handle returned by CascFindFirstFile.</summary>
    [DllImport(DllName, EntryPoint = "CascFindClose",
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascFindClose(IntPtr hFind);

    /// <summary>Opens a file within the CASC storage by its virtual path name.</summary>
    /// <param name="hStorage">Storage handle.</param>
    /// <param name="szFileName">Virtual file path (e.g. "data\global\ui\...").</param>
    /// <param name="dwLocale">Locale; pass 0 for default.</param>
    /// <param name="dwFlags">Open flags; use CASC_OPEN_BY_NAME (0).</param>
    /// <param name="phFile">Receives the file handle on success.</param>
    [DllImport(DllName, EntryPoint = "CascOpenFile", CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascOpenFile(IntPtr hStorage, string szFileName,
        uint dwLocale, uint dwFlags, out IntPtr phFile);

    /// <summary>Returns the size of an open CASC file.</summary>
    /// <param name="hFile">File handle from CascOpenFile.</param>
    /// <param name="pdwFileSizeHigh">High 32 bits of size (for files &gt; 4 GB). Usually 0.</param>
    /// <returns>Low 32 bits of the file size, or CASC_INVALID_SIZE on failure.</returns>
    [DllImport(DllName, EntryPoint = "CascGetFileSize",
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    internal static extern uint CascGetFileSize(IntPtr hFile, out uint pdwFileSizeHigh);

    /// <summary>Reads data from an open CASC file.</summary>
    /// <param name="hFile">File handle.</param>
    /// <param name="lpBuffer">Buffer to receive the data.</param>
    /// <param name="dwToRead">Number of bytes to read.</param>
    /// <param name="pdwRead">Receives the number of bytes actually read.</param>
    [DllImport(DllName, EntryPoint = "CascReadFile",
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascReadFile(IntPtr hFile, byte[] lpBuffer, uint dwToRead, out uint pdwRead);

    /// <summary>Closes a file handle opened by CascOpenFile.</summary>
    [DllImport(DllName, EntryPoint = "CascCloseFile",
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascCloseFile(IntPtr hFile);

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Returns true if CascLib.dll exists next to the executable.</summary>
    internal static bool IsDllPresent()
    {
        string dllPath = Path.Combine(AppContext.BaseDirectory, DllName);
        return File.Exists(dllPath);
    }

    /// <summary>
    /// Enumerates all locally-available files in the storage whose virtual path begins with
    /// one of the given prefixes. Yields (virtualPath, fileSize) tuples.
    /// <para>
    /// <paramref name="onScanProgress"/> is called approximately every 500 ms with the current
    /// file name being scanned (including non-matching files), so the caller can update the UI
    /// even during the long non-matching-file scan phase.
    /// </para>
    /// <para>
    /// <b>DLL bug workaround:</b> this build of CascLib never returns <c>false</c> from
    /// <c>CascFindNextFile</c> — it loops indefinitely. Enumeration is terminated via a
    /// "dry spell" heuristic: after finding the first matching file, if
    /// <c>DrySpellThreshold</c> consecutive non-matching entries are seen the scan stops.
    /// </para>
    /// </summary>
    internal static IEnumerable<(string VirtualPath, ulong FileSize)> EnumerateFiles(
        IntPtr hStorage,
        string[] prefixFilters,
        CancellationToken ct,
        Action<string>? onScanProgress = null,
        Action<long>? onIndexBuildComplete = null,
        Action<string>? onDiagnosticLog = null)
    {
        var indexSw = System.Diagnostics.Stopwatch.StartNew();
        IntPtr hFind = CascFindFirstFile(hStorage, "*", out CASC_FIND_DATA findData, null);
        indexSw.Stop();
        if (hFind == IntPtr.Zero)
            yield break;

        onIndexBuildComplete?.Invoke(indexSw.ElapsedMilliseconds);

        // This DLL never returns false from CascFindNextFile — it loops indefinitely.
        // Termination strategy: "dry spell" detection.
        //   • After finding at least one matching file, count consecutive non-matching entries.
        //   • Stop when that count reaches DrySpellThreshold (all real D2R data is clustered
        //     early in the listing; a gap this large means we're past the real content).
        //   • Before the first match, a hard cap prevents infinite spin if nothing ever matches.
        const long DrySpellThreshold = 1_000_000;  // ~71 ms at 14M entries/sec — safe margin
        const long PreMatchHardCap   = 20_000_000; // bail out if no match found in first 20M

        long totalEntries          = 0;
        long entriesSinceLastMatch = 0;
        bool foundAnyMatch         = false;
        var  sw                    = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            do
            {
                if (ct.IsCancellationRequested)
                    yield break;

                totalEntries++;

                string? fname = findData.szFileName;

                // Milestone progress every 500 k entries.
                if (totalEntries % 500_000 == 0)
                    onDiagnosticLog?.Invoke($"CASC scan: {totalEntries:N0} entries processed so far…");

                // Keep the UI alive with a progress ping every ~500 ms.
                if (onScanProgress != null && sw.ElapsedMilliseconds >= 500)
                {
                    onScanProgress(fname ?? string.Empty);
                    sw.Restart();
                }

                // Try to match this entry.
                bool yielded = false;
                if (findData.bFileAvailable != 0 && !string.IsNullOrEmpty(fname))
                {
                    // Normalize path separator: some CascLib builds return forward slashes.
                    string name = fname.Replace('/', '\\');
                    foreach (var prefix in prefixFilters)
                    {
                        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            yield return (name, findData.FileSize);
                            yielded = true;
                            break;
                        }
                    }
                }

                if (yielded)
                {
                    entriesSinceLastMatch = 0;
                    foundAnyMatch = true;
                }
                else if (foundAnyMatch)
                {
                    if (++entriesSinceLastMatch >= DrySpellThreshold)
                    {
                        onDiagnosticLog?.Invoke(
                            $"CASC scan: stopping — {DrySpellThreshold:N0} consecutive non-matching " +
                            $"entries after last match (total scanned: {totalEntries:N0}).");
                        break;
                    }
                }
                else if (totalEntries >= PreMatchHardCap)
                {
                    onDiagnosticLog?.Invoke(
                        $"CASC scan: aborting — no matches found in first {PreMatchHardCap:N0} entries.");
                    break;
                }

            } while (CascFindNextFile(hFind, out findData));
        }
        finally
        {
            CascFindClose(hFind);
        }
    }
}
