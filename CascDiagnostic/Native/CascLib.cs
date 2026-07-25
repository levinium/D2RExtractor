using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CascDiagnostic.Native;

/// <summary>
/// P/Invoke declarations for Ladislav Zezula's CascLib.dll.
/// Copied from D2RExtractor for use in the diagnostic tool.
/// </summary>
internal static class CascLib
{
    private const string DllName = "CascLib.dll";

    internal const int CASC_MAX_PATH = 260; // Windows MAX_PATH

    internal const uint CASC_INVALID_ID = 0xFFFFFFFF;

    // CascOpenFile flags
    internal const uint CASC_OPEN_BY_NAME = 0x00000000;
    internal const uint CASC_OPEN_BY_DATAFILE_NUMBER = 0x00000001;
    internal const uint CASC_OPEN_BY_CKEY = 0x00000002;
    internal const uint CASC_OPEN_BY_EKEY = 0x00000003;

    // CascOpenStorageEx feature flags
    internal const uint CASC_FEATURE_ONLINE = 0x00000400;
    internal const uint CASC_FEATURE_FORCE_DOWNLOAD = 0x00001000;
    internal const uint CASC_FEATURE_ALLOW_DOWNLOAD = 0x00002000;

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Ansi)]
    internal struct CASC_FIND_DATA
    {
        [FieldOffset(0)]
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CASC_MAX_PATH)]
        public string szFileName;

        [FieldOffset(296)]
        public ulong TagBitMask;

        [FieldOffset(304)]
        public ulong FileSize;

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

        [FieldOffset(336)]
        public uint bFileAvailable;

        [FieldOffset(340)]
        public uint NameType;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CASC_OPEN_STORAGE_ARGS
    {
        public IntPtr Size;
        public IntPtr szLocalPath;
        public IntPtr szCodeName;
        public IntPtr szRegion;
        public IntPtr PfnProgressCallback;
        public IntPtr PtrProgressParam;
        public IntPtr PfnProductCallback;
        public IntPtr PtrProductParam;
        public uint dwLocaleMask;
        public uint dwFlags;
        public IntPtr szBuildKey;
        public IntPtr szCdnHostUrl;
    }

    [DllImport(DllName, EntryPoint = "CascOpenStorage", CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascOpenStorage(string szDataPath, uint dwLocaleMask, out IntPtr phStorage);

    [DllImport(DllName, EntryPoint = "CascOpenStorageEx", CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool CascOpenStorageEx(
        string? szParams,
        ref CASC_OPEN_STORAGE_ARGS pArgs,
        [MarshalAs(UnmanagedType.U1)] bool bOnlineStorage,
        out IntPtr phStorage);

    [DllImport(DllName, EntryPoint = "CascCloseStorage",
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascCloseStorage(IntPtr hStorage);

    [DllImport(DllName, EntryPoint = "CascFindFirstFile", CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    internal static extern IntPtr CascFindFirstFile(IntPtr hStorage, string szMask,
        out CASC_FIND_DATA pFindData, string? szListFile);

    [DllImport(DllName, EntryPoint = "CascFindNextFile",
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascFindNextFile(IntPtr hFind, out CASC_FIND_DATA pFindData);

    [DllImport(DllName, EntryPoint = "CascFindClose",
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascFindClose(IntPtr hFind);

    [DllImport(DllName, EntryPoint = "CascOpenFile", CharSet = CharSet.Ansi,
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascOpenFile(IntPtr hStorage, string szFileName,
        uint dwLocale, uint dwFlags, out IntPtr phFile);

    [DllImport(DllName, EntryPoint = "CascGetFileSize",
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    internal static extern uint CascGetFileSize(IntPtr hFile, out uint pdwFileSizeHigh);

    [DllImport(DllName, EntryPoint = "CascReadFile",
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascReadFile(IntPtr hFile, byte[] lpBuffer, uint dwToRead, out uint pdwRead);

    [DllImport(DllName, EntryPoint = "CascCloseFile",
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascCloseFile(IntPtr hFile);

    // CascGetStorageInfo
    private const uint CascStorageLocalFileCount = 0;
    private const uint CascStorageTotalFileCount = 1;

    [DllImport(DllName, EntryPoint = "CascGetStorageInfo",
        CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CascGetStorageInfo(
        IntPtr hStorage, uint InfoClass, ref uint pvStorageInfo,
        uint cbStorageInfo, ref uint pcbLengthNeeded);

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    internal static IntPtr OpenStorageWithFallback(string installPath, Action<string>? log)
    {
        System.Diagnostics.Debug.Assert(
            Marshal.SizeOf<CASC_OPEN_STORAGE_ARGS>() == 88,
            $"CASC_OPEN_STORAGE_ARGS size mismatch: expected 88, got {Marshal.SizeOf<CASC_OPEN_STORAGE_ARGS>()}");

        string[] candidatePaths = new[]
        {
            installPath,
            Path.Combine(installPath, "Data"),
        };

        foreach (string basePath in candidatePaths)
        {
            foreach (string file in new[] { ".build.info", ".build.db", ".product.db" })
            {
                string full = Path.Combine(basePath, file);
                log?.Invoke($"  [{(File.Exists(full) ? "FOUND" : "missing")}] {full}");
            }
        }

        var errors = new List<string>();

        foreach (string cascPath in candidatePaths)
        {
            if (CascOpenStorage(cascPath, 0, out IntPtr hStorage) && hStorage != IntPtr.Zero)
            {
                if (cascPath != installPath)
                    log?.Invoke($"CASC storage opened at alternate path: {cascPath}");
                return hStorage;
            }

            int err = Marshal.GetLastWin32Error();
            errors.Add($"CascOpenStorage('{cascPath}') → error {err}");
            log?.Invoke($"CascOpenStorage failed for '{cascPath}' (Win32 error {err}).");

            try
            {
                var args1 = MakeStorageArgs(CASC_FEATURE_ONLINE | CASC_FEATURE_ALLOW_DOWNLOAD);
                log?.Invoke($"Trying CascOpenStorageEx with ONLINE+ALLOW_DOWNLOAD for '{cascPath}'…");
                if (CascOpenStorageEx(cascPath, ref args1, false, out hStorage) && hStorage != IntPtr.Zero)
                {
                    log?.Invoke($"CASC storage opened successfully with CDN support at '{cascPath}'.");
                    return hStorage;
                }

                err = Marshal.GetLastWin32Error();
                errors.Add($"CascOpenStorageEx('{cascPath}', ONLINE+ALLOW_DOWNLOAD) → error {err}");
                log?.Invoke($"CascOpenStorageEx (ONLINE+ALLOW_DOWNLOAD) failed for '{cascPath}' (Win32 error {err}).");

                var args3 = MakeStorageArgs(CASC_FEATURE_ONLINE | CASC_FEATURE_ALLOW_DOWNLOAD);
                if (CascOpenStorageEx(cascPath, ref args3, true, out hStorage) && hStorage != IntPtr.Zero)
                {
                    log?.Invoke($"CASC storage opened in full online mode at '{cascPath}'.");
                    return hStorage;
                }

                err = Marshal.GetLastWin32Error();
                errors.Add($"CascOpenStorageEx('{cascPath}', online) → error {err}");
                log?.Invoke($"CascOpenStorageEx (online) failed for '{cascPath}' (Win32 error {err}).");
            }
            catch (EntryPointNotFoundException)
            {
                errors.Add($"CascOpenStorageEx not exported (DLL too old)");
                log?.Invoke("CascOpenStorageEx is not available in this CascLib.dll. " +
                            "Please update to CascLib.dll 3.0+ for improved compatibility.");
                break;
            }
        }

        throw new InvalidOperationException(
            "All CASC open attempts failed.\n" +
            string.Join("\n", errors) + "\n\n" +
            $"Ensure '{installPath}' is a valid D2R installation folder.");
    }

    private static CASC_OPEN_STORAGE_ARGS MakeStorageArgs(uint dwFlags)
    {
        var args = new CASC_OPEN_STORAGE_ARGS();
        args.Size = (IntPtr)Marshal.SizeOf<CASC_OPEN_STORAGE_ARGS>();
        args.dwFlags = dwFlags;
        return args;
    }

    internal static long GetTotalFileCount(IntPtr hStorage)
    {
        uint value = 0, needed = 0;
        if (CascGetStorageInfo(hStorage, CascStorageTotalFileCount, ref value, 4, ref needed) && value > 0)
            return value;
        if (CascGetStorageInfo(hStorage, CascStorageLocalFileCount, ref value, 4, ref needed) && value > 0)
            return value;
        return -1;
    }

    /// <summary>
    /// Enumerates all files in the storage whose virtual path begins with
    /// one of the given prefixes. Pass new[] { "" } to match ALL files.
    /// </summary>
    internal static IEnumerable<(string VirtualPath, ulong FileSize, uint LocaleFlags, uint ContentFlags, uint bFileAvailable)> EnumerateFilesEx(
        IntPtr hStorage,
        string[] prefixFilters,
        CancellationToken ct,
        Action<string>? onScanProgress = null,
        Action<long>? onIndexBuildComplete = null,
        Action<string>? onDiagnosticLog = null)
    {
        long knownFileCount = GetTotalFileCount(hStorage);
        const long FallbackHardCap = 30_000_000;
        long iterationCap;

        if (knownFileCount > 0)
        {
            iterationCap = knownFileCount + (knownFileCount / 10);
            onDiagnosticLog?.Invoke(
                $"CASC storage reports {knownFileCount:N0} files — iteration cap set to {iterationCap:N0}.");
        }
        else
        {
            iterationCap = FallbackHardCap;
            onDiagnosticLog?.Invoke(
                $"CascGetStorageInfo unavailable — using fallback cap of {FallbackHardCap:N0}.");
        }

        var indexSw = System.Diagnostics.Stopwatch.StartNew();
        IntPtr hFind = CascFindFirstFile(hStorage, "*", out CASC_FIND_DATA findData, null);
        indexSw.Stop();
        if (hFind == IntPtr.Zero)
            yield break;

        onIndexBuildComplete?.Invoke(indexSw.ElapsedMilliseconds);

        long totalEntries = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            do
            {
                if (ct.IsCancellationRequested)
                    yield break;

                totalEntries++;
                if (totalEntries > iterationCap)
                {
                    onDiagnosticLog?.Invoke(
                        $"CASC scan: iteration cap of {iterationCap:N0} reached — stopping enumeration.");
                    break;
                }

                string? fname = findData.szFileName;

                if (totalEntries % 500_000 == 0)
                    onDiagnosticLog?.Invoke($"CASC scan: {totalEntries:N0} entries processed so far…");

                if (onScanProgress != null && sw.ElapsedMilliseconds >= 500)
                {
                    onScanProgress(fname ?? string.Empty);
                    sw.Restart();
                }

                if (!string.IsNullOrEmpty(fname))
                {
                    string name = fname.Replace('/', '\\');
                    foreach (var prefix in prefixFilters)
                    {
                        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            yield return (name, findData.FileSize, findData.dwLocaleFlags, findData.dwContentFlags, findData.bFileAvailable);
                            break;
                        }
                    }
                }

            } while (CascFindNextFile(hFind, out findData));
        }
        finally
        {
            CascFindClose(hFind);
        }
    }
}
