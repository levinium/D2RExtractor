using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using D2RExtractor.Native;
using D2RExtractor.Services.Steam;

namespace D2RExtractor.Services;

/// <summary>
/// Abstraction over the two D2R storage formats so a single extraction loop can
/// serve both:
///   • <see cref="CascLibBackend"/>   — classic CASC via CascLib.dll (Battle.net).
///   • <see cref="SteamStaticBackend"/> — native reader for Steam's static-container
///     format (patch build 93236+), which no longer uses classic CASC.
///
/// The service enumerates matching files, then extracts each one; the backend
/// owns whatever storage handles/resources that requires.
/// </summary>
internal interface IExtractionBackend : IDisposable
{
    /// <summary>
    /// Enumerates files whose virtual path starts with one of <paramref name="prefixes"/>,
    /// returning <c>(virtualPath, fileSize)</c> pairs. Virtual paths use the CascLib
    /// namespace form (e.g. <c>data:data\global\…</c>) for both backends.
    /// </summary>
    List<(string VirtualPath, ulong FileSize)> EnumerateMatching(
        string[] prefixes, CancellationToken ct,
        Action<string>? onScanProgress, Action<long>? onIndexBuildComplete, Action<string>? log);

    /// <summary>Called once before the per-file extraction loop (e.g. to open a fresh storage handle).</summary>
    void PrepareExtraction(Action<string>? log);

    /// <summary>
    /// Extracts a single file to <paramref name="destPath"/>. Returns false if the file
    /// could not be opened/read (e.g. a CDN-only entry not present locally).
    /// <paramref name="buffer"/> is a reusable scratch buffer the backend may use.
    /// </summary>
    bool ExtractFile(string virtualPath, string destPath, byte[] buffer);

    /// <summary>Called once after the extraction loop completes (e.g. to close a storage handle).</summary>
    void FinishExtraction();
}

/// <summary>
/// Classic-CASC backend backed by CascLib.dll. Used for Battle.net installs
/// (and any install where CascLib can still open the storage). Enumeration and
/// extraction each use their own storage handle, mirroring the original logic.
/// </summary>
internal sealed class CascLibBackend : IExtractionBackend
{
    private const int ChunkSize = 1024 * 1024; // 1 MB read buffer
    private readonly string _installPath;
    private IntPtr _extractStorage = IntPtr.Zero;

    public CascLibBackend(string installPath) => _installPath = installPath;

    public List<(string VirtualPath, ulong FileSize)> EnumerateMatching(
        string[] prefixes, CancellationToken ct,
        Action<string>? onScanProgress, Action<long>? onIndexBuildComplete, Action<string>? log)
    {
        var files = new List<(string, ulong)>();
        IntPtr hStorage = CascLib.OpenStorageWithFallback(_installPath, log);
        try
        {
            foreach (var entry in CascLib.EnumerateFiles(hStorage, prefixes, ct, onScanProgress, onIndexBuildComplete, log))
                files.Add(entry);
        }
        finally
        {
            CascLib.CascCloseStorage(hStorage);
        }
        return files;
    }

    public void PrepareExtraction(Action<string>? log)
    {
        // A fresh handle for the extraction phase (the enumeration handle was closed).
        _extractStorage = CascLib.OpenStorageWithFallback(_installPath, log);
    }

    public bool ExtractFile(string virtualPath, string destPath, byte[] buffer)
    {
        if (!CascLib.CascOpenFile(_extractStorage, virtualPath, 0, CascLib.CASC_OPEN_BY_NAME, out IntPtr hFile)
            || hFile == IntPtr.Zero)
            return false;

        try
        {
            uint sizeLow = CascLib.CascGetFileSize(hFile, out uint sizeHigh);
            if (sizeLow == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0)
                return false;

            long totalSize = ((long)sizeHigh << 32) | sizeLow;
            if (totalSize == 0)
            {
                File.WriteAllBytes(destPath, Array.Empty<byte>());
                return true;
            }

            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 65536, useAsync: false);
            fs.SetLength(totalSize);

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
        return true;
    }

    public void FinishExtraction()
    {
        if (_extractStorage != IntPtr.Zero)
        {
            CascLib.CascCloseStorage(_extractStorage);
            _extractStorage = IntPtr.Zero;
        }
    }

    public void Dispose() => FinishExtraction();
}

/// <summary>
/// Native backend for Steam's static-container storage (patch build 93236+).
/// Parses the storage entirely from local files — no CascLib.dll and no internet.
/// </summary>
internal sealed class SteamStaticBackend : IExtractionBackend
{
    private readonly SteamStaticStorage _storage;

    public SteamStaticBackend(string installPath, Action<string>? log)
    {
        _storage = SteamStaticStorage.Open(installPath, log);
    }

    public List<(string VirtualPath, ulong FileSize)> EnumerateMatching(
        string[] prefixes, CancellationToken ct,
        Action<string>? onScanProgress, Action<long>? onIndexBuildComplete, Action<string>? log)
    {
        var files = new List<(string, ulong)>();
        foreach (var entry in _storage.EnumerateFiles(prefixes, ct, onScanProgress, onIndexBuildComplete, log))
            files.Add(entry);
        return files;
    }

    public void PrepareExtraction(Action<string>? log) { /* nothing to open — storage is already loaded */ }

    public bool ExtractFile(string virtualPath, string destPath, byte[] buffer) =>
        _storage.ExtractFile(virtualPath, destPath);

    public void FinishExtraction() { /* handles are released on Dispose */ }

    public void Dispose() => _storage.Dispose();
}
