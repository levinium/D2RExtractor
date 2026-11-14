using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace D2RExtractor.Services.Steam;

/// <summary>
/// Reads files from a Steam D2R "static container" — the flat set of
/// <c>NN-NNNNNNNN.data</c> archive files in the game's <c>data\</c> folder.
///
/// Unlike the classic Battle.net layout (separate <c>*.idx</c> index files),
/// the physical location of every file is encoded directly in its 16-byte
/// encoding key (EKey). The top 8 bytes of the EKey, read big-endian, are
/// sliced into bit fields per the <c>key-layout-K</c> descriptor:
/// <code>
///   [ padding | index bits | chunk bits | archive bits | offset bits ]
/// </code>
/// yielding (chunk, archive, byteOffset). The target file is
/// <c>{chunk:D2}-{archive:x8}.data</c>, and the blob lives at <c>byteOffset</c>.
///
/// Ported/adapted from rustydemon (rustydemon-lib/src/static_container.rs), with
/// the file-naming specialised to D2R's flat scheme (D4 uses <c>-meta/-payload</c>
/// split files in numbered sub-directories instead).
/// </summary>
internal sealed class StaticContainer : IDisposable
{
    private readonly string _dataDir;
    private readonly int _indexBits;
    private readonly SteamBuildConfig.KeyLayout?[] _layouts;
    // Cached read-only handles for random BLTE reads, keyed by data-file name.
    private readonly Dictionary<string, FileStream> _handles = new(StringComparer.OrdinalIgnoreCase);

    private StaticContainer(string dataDir, int indexBits, SteamBuildConfig.KeyLayout?[] layouts)
    {
        _dataDir = dataDir;
        _indexBits = indexBits;
        _layouts = layouts;
    }

    /// <summary>Builds a container over <paramref name="dataDir"/> using the parsed build config.</summary>
    public static StaticContainer FromConfig(string dataDir, SteamBuildConfig config)
    {
        int indexBits = config.KeyLayoutIndexBits;
        if (indexBits < 0 || indexBits > 8)
            throw new InvalidDataException($"static container: key-layout-index-bits {indexBits} out of range 0..8.");

        int slots = 1 << indexBits;
        var layouts = new SteamBuildConfig.KeyLayout?[slots];
        foreach (var kv in config.KeyLayouts)
        {
            if (kv.Key >= slots)
                throw new InvalidDataException($"static container: key-layout-{kv.Key} out of range for index-bits {indexBits}.");
            layouts[kv.Key] = kv.Value;
        }
        return new StaticContainer(dataDir, indexBits, layouts);
    }

    /// <summary>Physical location decoded from an EKey.</summary>
    public readonly struct Location
    {
        public readonly int Chunk;
        public readonly long Archive;
        public readonly long ByteOffset;
        public readonly int OffsetBits;
        public Location(int chunk, long archive, long byteOffset, int offsetBits)
        {
            Chunk = chunk; Archive = archive; ByteOffset = byteOffset; OffsetBits = offsetBits;
        }
    }

    /// <summary>Extracts (chunk, archive, byteOffset) from an EKey's embedded location bits.</summary>
    public Location ExtractLocation(byte[] ekey)
    {
        // Top 8 bytes as a big-endian u64. Bits 56..63 are unused padding; the
        // layout-index field begins at bit (56 - indexBits).
        ulong hi = 0;
        for (int i = 8; i < 16; i++) hi = (hi << 8) | ekey[i];

        int layoutIdxOff = 56 - _indexBits;
        int layoutIndex = (int)ExtractBits(hi, layoutIdxOff, _indexBits);

        if (layoutIndex >= _layouts.Length || _layouts[layoutIndex] is not { } layout)
            throw new InvalidDataException($"static container: no key-layout defined for index {layoutIndex}.");

        int chunkOff = layoutIdxOff - layout.ChunkBits;
        int chunk = (int)ExtractBits(hi, chunkOff, layout.ChunkBits);

        int archiveOff = chunkOff - layout.ArchiveBits;
        long archive = (long)ExtractBits(hi, archiveOff, layout.ArchiveBits);

        int offsetOff = archiveOff - layout.OffsetBits;
        long rawOffset = (long)ExtractBits(hi, offsetOff, layout.OffsetBits);

        long byteOffset = layout.Flags == 0 ? rawOffset : rawOffset * layout.Flags;
        return new Location(chunk, archive, byteOffset, layout.OffsetBits);
    }

    /// <summary>Full path of the data file that holds <paramref name="loc"/>.</summary>
    public string DataFilePath(Location loc) =>
        Path.Combine(_dataDir, $"{loc.Chunk:D2}-{loc.Archive:x8}.data");

    /// <summary>
    /// Reads and decodes the logical bytes stored at <paramref name="ekey"/>'s location.
    /// The blob is auto-detected as BLTE ('BL' magic) or a raw zlib stream (0x78,
    /// used for the <c>espec = z</c> VFS roots).
    /// </summary>
    public byte[] OpenByEKey(byte[] ekey)
    {
        Location loc = ExtractLocation(ekey);
        string path = DataFilePath(loc);

        FileStream handle = GetHandle(path);
        handle.Seek(loc.ByteOffset, SeekOrigin.Begin);

        Span<byte> head = stackalloc byte[2];
        if (handle.Read(head) != 2)
            throw new EndOfStreamException($"static container: could not read blob header at offset {loc.ByteOffset} in {Path.GetFileName(path)}.");

        if (head[0] == (byte)'B' && head[1] == (byte)'L')
        {
            byte[] raw = ReadBlteFramed(handle, loc.ByteOffset);
            return Blte.Decode(raw);
        }
        if (head[0] == 0x78)
        {
            // Raw zlib stream (TVFS VFS root). Use a fresh, exclusive stream so
            // ZLibStream's read-ahead doesn't disturb the shared handle.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(loc.ByteOffset, SeekOrigin.Begin);
            using var z = new ZLibStream(fs, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            z.CopyTo(outMs);
            return outMs.ToArray();
        }

        throw new InvalidDataException(
            $"static container: unrecognised blob header {head[0]:X2} {head[1]:X2} at offset {loc.ByteOffset} in {Path.GetFileName(path)}.");
    }

    /// <summary>
    /// Reads the raw (still-encoded) BLTE blob at an EKey's location, without decoding.
    /// Used for integrity verification (block-hash checks). Only valid for BLTE blobs.
    /// </summary>
    public byte[] ReadRawBlte(byte[] ekey)
    {
        Location loc = ExtractLocation(ekey);
        FileStream handle = GetHandle(DataFilePath(loc));
        return ReadBlteFramed(handle, loc.ByteOffset);
    }

    /// <summary>
    /// Reads a complete BLTE blob starting at <paramref name="offset"/>, parsing
    /// the header to determine its total on-disk length, and returns the raw bytes.
    /// </summary>
    private static byte[] ReadBlteFramed(FileStream handle, long offset)
    {
        handle.Seek(offset, SeekOrigin.Begin);

        byte[] head = new byte[8];
        ReadExact(handle, head, 0, 8);

        uint magic = BitConverter.ToUInt32(head, 0);
        if (magic != 0x45544C42) // 'BLTE'
            throw new InvalidDataException($"static container: expected BLTE magic, got 0x{magic:X8}.");

        int headerSize = (head[4] << 24) | (head[5] << 16) | (head[6] << 8) | head[7];
        if (headerSize < 12)
            throw new InvalidDataException($"static container: BLTE header size {headerSize} too small (headerless blocks unsupported).");

        byte[] buf = new byte[headerSize];
        Array.Copy(head, buf, 8);
        ReadExact(handle, buf, 8, headerSize - 8);

        if (buf[8] != 0x0F)
            throw new InvalidDataException($"static container: BLTE frame flag 0x{buf[8]:X2} != 0x0F.");

        int numBlocks = (buf[9] << 16) | (buf[10] << 8) | buf[11];
        int expected = 12 + numBlocks * 24;
        if (expected != headerSize)
            throw new InvalidDataException($"static container: BLTE header size {headerSize} != expected {expected}.");

        long totalPayload = 0;
        for (int i = 0; i < numBlocks; i++)
        {
            int off = 12 + i * 24;
            int comp = (buf[off] << 24) | (buf[off + 1] << 16) | (buf[off + 2] << 8) | buf[off + 3];
            totalPayload += comp;
        }

        int totalLen = checked((int)(headerSize + totalPayload));
        byte[] full = new byte[totalLen];
        Array.Copy(buf, full, headerSize);
        ReadExact(handle, full, headerSize, totalLen - headerSize);
        return full;
    }

    private FileStream GetHandle(string path)
    {
        if (_handles.TryGetValue(path, out FileStream? fs))
            return fs;
        if (!File.Exists(path))
            throw new FileNotFoundException($"static container: data file not found: {path}");
        fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: false);
        _handles[path] = fs;
        return fs;
    }

    private static void ReadExact(Stream s, byte[] buffer, int offset, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buffer, offset + read, count - read);
            if (n == 0) throw new EndOfStreamException("static container: unexpected end of data file.");
            read += n;
        }
    }

    private static ulong ExtractBits(ulong value, int start, int count)
    {
        if (count == 0) return 0;
        ulong mask = count >= 64 ? ulong.MaxValue : (1UL << count) - 1;
        return (value >> start) & mask;
    }

    public void Dispose()
    {
        foreach (var fs in _handles.Values) fs.Dispose();
        _handles.Clear();
    }
}
