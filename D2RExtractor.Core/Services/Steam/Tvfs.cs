using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace D2RExtractor.Services.Steam;

/// <summary>
/// Parser for the TVFS (TACT Virtual File System) directory format that maps
/// human-readable virtual paths to encoding keys. D2R's Steam storage stores its
/// file tree as one or more zlib-compressed TVFS directories referenced by the
/// <c>vfs-root</c> / <c>vfs-N</c> entries in <c>.build.config</c>.
///
/// The walker resolves the whole tree — recursing into sub-directory VFS blobs —
/// and reports every file leaf as a <see cref="FileEntry"/> carrying its virtual
/// path and the ordered list of storage spans (each an EKey + logical range) that
/// make up the file's content.
///
/// Ported from rustydemon (rustydemon-lib/src/root/tvfs.rs). Jenkins-hash path
/// indexing is omitted — for extraction we only need the path string and EKeys.
/// </summary>
internal sealed class Tvfs
{
    private const uint TvfsMagic = 0x53465654; // 'TVFS' little-endian

    private const uint PteSeparatorPre = 0x0001;
    private const uint PteSeparatorPost = 0x0002;
    private const uint PteNodeValue = 0x0004;

    private const uint FolderNode = 0x80000000;
    private const uint FolderSizeMask = 0x7FFFFFFF;

    /// <summary>One storage span of a file: a logical range backed by an encoding key.</summary>
    public readonly struct Span
    {
        public readonly long ContentOffset; // offset of this span within the logical file
        public readonly long ContentSize;   // number of logical bytes this span contributes
        public readonly byte[] EKey;        // encoding key of the stored blob
        public Span(long contentOffset, long contentSize, byte[] eKey)
        {
            ContentOffset = contentOffset; ContentSize = contentSize; EKey = eKey;
        }
    }

    /// <summary>A resolved file leaf.</summary>
    public sealed class FileEntry
    {
        public required string VirtualPath { get; init; } // CASC-style, e.g. "data:data\global\allcofs.bin"
        public required long Size { get; init; }
        public required List<Span> Spans { get; init; }

        /// <summary>
        /// Lower-case hex content key, joined on from the text ROOT (see
        /// <c>SteamStaticStorage.ApplyTextRootPaths</c>). Null when the ROOT did not supply one —
        /// the TVFS itself carries only encoding keys, not a content hash.
        /// </summary>
        public string? ContentKey { get; init; }
    }

    private sealed class Header
    {
        public int EKeySize;
        public byte[] PathTable = Array.Empty<byte>();
        public byte[] VfsTable = Array.Empty<byte>();
        public byte[] CftTable = Array.Empty<byte>();
        public int CftOffsSize;
    }

    private readonly StaticContainer _container;
    // 9-byte EKey prefixes of sub-directory VFS blobs → full EKey.
    private readonly Dictionary<string, byte[]> _vfsSubDirs;
    private readonly List<FileEntry> _files = new();
    // Guards against pathological cyclic sub-directory references.
    private int _recursionDepth;
    private const int MaxRecursionDepth = 64;

    private Tvfs(StaticContainer container, Dictionary<string, byte[]> vfsSubDirs)
    {
        _container = container;
        _vfsSubDirs = vfsSubDirs;
    }

    /// <summary>
    /// Parses the full TVFS tree starting from <paramref name="vfsRootEKey"/> and
    /// returns every file leaf found.
    /// </summary>
    public static List<FileEntry> Parse(
        StaticContainer container,
        byte[] vfsRootEKey,
        IReadOnlyList<byte[]> vfsSubDirectoryEKeys)
    {
        var subDirs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (byte[] ek in vfsSubDirectoryEKeys)
            subDirs[Key9(ek)] = ek;

        var tvfs = new Tvfs(container, subDirs);
        byte[] rootData = container.OpenByEKey(vfsRootEKey);
        Header header = ParseHeader(rootData);

        var pathBuf = new List<byte>(512);
        tvfs.ParseDirectoryData(header, pathBuf);
        return tvfs._files;
    }

    private void ParseDirectoryData(Header header, List<byte> pathBuf)
    {
        var table = new ReadOnlySpan<byte>(header.PathTable);

        // A leading 0xFF + folder node value marks the whole directory.
        if (table.Length > 5 && table[0] == 0xFF)
        {
            uint nodeValue = ReadU32BE(table.Slice(1, 4));
            if ((nodeValue & FolderNode) == 0)
                throw new InvalidDataException("TVFS: root path-table entry is not a folder.");
            table = table[5..];
        }

        ParsePathFileTable(header, pathBuf, table);
    }

    private void ParsePathFileTable(Header header, List<byte> pathBuf, ReadOnlySpan<byte> table)
    {
        int savePos = pathBuf.Count;

        while (!table.IsEmpty)
        {
            table = CapturePathEntry(table, out byte[] name, out uint nodeFlags, out uint nodeValue);

            if ((nodeFlags & PteSeparatorPre) != 0) pathBuf.Add((byte)'/');
            pathBuf.AddRange(name);
            if ((nodeFlags & PteSeparatorPost) != 0) pathBuf.Add((byte)'/');

            if ((nodeFlags & PteNodeValue) != 0)
            {
                if ((nodeValue & FolderNode) != 0)
                {
                    // Inline sub-directory: its entries occupy the next (size - 4) bytes.
                    int dirLen = (int)(nodeValue & FolderSizeMask) - 4;
                    if (dirLen < 0 || dirLen > table.Length)
                        throw new InvalidDataException("TVFS: folder size exceeds remaining path table.");
                    ReadOnlySpan<byte> subTable = table[..dirLen];
                    ParsePathFileTable(header, pathBuf, subTable);
                    table = table[dirLen..];
                }
                else
                {
                    HandleFileLeaf(header, pathBuf, (int)nodeValue);
                }

                pathBuf.RemoveRange(savePos, pathBuf.Count - savePos);
            }
        }
    }

    private void HandleFileLeaf(Header header, List<byte> pathBuf, int vfsOffset)
    {
        byte[] vfs = header.VfsTable;
        if (vfsOffset >= vfs.Length) return;

        int spanCount = vfs[vfsOffset];
        if (spanCount == 0 || spanCount > 224) return;

        int vfsPos = vfsOffset + 1;

        if (spanCount == 1)
        {
            Span span = ReadSpan(header, ref vfsPos);
            string key9 = Key9(span.EKey);
            if (_vfsSubDirs.TryGetValue(key9, out byte[]? fullEKey))
            {
                // Sub-directory reference: open and recurse (path already ends at this node).
                if (_recursionDepth >= MaxRecursionDepth) return;
                pathBuf.Add((byte)'/');
                try
                {
                    _recursionDepth++;
                    byte[] subData = _container.OpenByEKey(fullEKey);
                    Header subHeader = ParseHeader(subData);
                    ParseDirectoryData(subHeader, pathBuf);
                }
                catch
                {
                    // Skip an inaccessible/corrupt sub-directory rather than aborting the whole walk.
                }
                finally
                {
                    _recursionDepth--;
                }
                return;
            }

            AddFileEntry(pathBuf, new List<Span> { span });
        }
        else
        {
            var spans = new List<Span>(spanCount);
            for (int i = 0; i < spanCount; i++)
                spans.Add(ReadSpan(header, ref vfsPos));
            AddFileEntry(pathBuf, spans);
        }
    }

    private static Span ReadSpan(Header header, ref int vfsPos)
    {
        byte[] vfs = header.VfsTable;
        int itemSize = 4 + 4 + header.CftOffsSize; // contentOffset + contentSize + cftOffset
        if (vfsPos + itemSize > vfs.Length)
            throw new InvalidDataException("TVFS: VFS span overflows table.");

        long contentOffset = ReadU32BE(vfs.AsSpan(vfsPos, 4));
        long contentSize = ReadU32BE(vfs.AsSpan(vfsPos + 4, 4));
        long cftOffset = ReadIntBE(vfs.AsSpan(vfsPos + 8, header.CftOffsSize), header.CftOffsSize);
        vfsPos += itemSize;

        int ekeySize = header.EKeySize;
        if (cftOffset + ekeySize > header.CftTable.Length)
            throw new InvalidDataException("TVFS: CFT offset out of bounds.");

        byte[] ekey = new byte[16];
        int copy = Math.Min(ekeySize, 16);
        Array.Copy(header.CftTable, (int)cftOffset, ekey, 0, copy);
        return new Span(contentOffset, contentSize, ekey);
    }

    private void AddFileEntry(List<byte> pathBuf, List<Span> spans)
    {
        string logicalPath = Encoding.UTF8.GetString(pathBuf.ToArray());
        string cascPath = ToCascVirtualPath(logicalPath);

        long size = 0;
        foreach (Span s in spans)
            size = Math.Max(size, s.ContentOffset + s.ContentSize);

        _files.Add(new FileEntry { VirtualPath = cascPath, Size = size, Spans = spans });
    }

    /// <summary>
    /// Converts a TVFS logical path (e.g. <c>data/data/global/allcofs.bin</c>) into the
    /// CascLib-style virtual path (e.g. <c>data:data\global\allcofs.bin</c>): the first
    /// segment becomes the mount namespace before a colon, the rest uses backslashes.
    /// This matches what CascLib returns for the Battle.net storage so all downstream
    /// path handling (prefix filtering, namespace stripping) is identical.
    /// </summary>
    private static string ToCascVirtualPath(string logicalPath)
    {
        int slash = logicalPath.IndexOf('/');
        if (slash < 0)
            return logicalPath; // top-level file with no directory
        string mount = logicalPath[..slash];
        string rest = logicalPath[(slash + 1)..].Replace('/', '\\');
        return $"{mount}:{rest}";
    }

    // ── Header parsing ─────────────────────────────────────────────────────────

    private static Header ParseHeader(byte[] data)
    {
        if (data.Length < 46)
            throw new InvalidDataException("TVFS: data too short for header.");

        uint magic = BitConverter.ToUInt32(data, 0);
        if (magic != TvfsMagic)
            throw new InvalidDataException($"TVFS: bad magic 0x{magic:X8}.");

        byte version = data[4];
        if (version != 1)
            throw new InvalidDataException($"TVFS: unsupported format version {version}.");

        int ekeySize = data[6];

        int pathTableOffset = (int)ReadU32BE(data.AsSpan(12, 4));
        int pathTableSize = (int)ReadU32BE(data.AsSpan(16, 4));
        int vfsTableOffset = (int)ReadU32BE(data.AsSpan(20, 4));
        int vfsTableSize = (int)ReadU32BE(data.AsSpan(24, 4));
        int cftTableOffset = (int)ReadU32BE(data.AsSpan(28, 4));
        int cftTableSize = (int)ReadU32BE(data.AsSpan(32, 4));

        byte[] Slice(int off, int sz)
        {
            if (off < 0 || sz < 0 || off + sz > data.Length)
                throw new InvalidDataException("TVFS: table extends past end of data.");
            byte[] b = new byte[sz];
            Array.Copy(data, off, b, 0, sz);
            return b;
        }

        return new Header
        {
            EKeySize = ekeySize,
            PathTable = Slice(pathTableOffset, pathTableSize),
            VfsTable = Slice(vfsTableOffset, vfsTableSize),
            CftTable = Slice(cftTableOffset, cftTableSize),
            CftOffsSize = OffsetFieldSize(cftTableSize),
        };
    }

    // ── Path-entry parsing ─────────────────────────────────────────────────────

    private static ReadOnlySpan<byte> CapturePathEntry(
        ReadOnlySpan<byte> table, out byte[] name, out uint nodeFlags, out uint nodeValue)
    {
        name = Array.Empty<byte>();
        nodeFlags = 0;
        nodeValue = 0;

        // Leading path separator (0x00).
        if (!table.IsEmpty && table[0] == 0x00)
        {
            nodeFlags |= PteSeparatorPre;
            table = table[1..];
        }

        // Length-prefixed name (unless the next byte is the 0xFF node-value marker).
        if (!table.IsEmpty && table[0] != 0xFF)
        {
            int len = table[0];
            if (1 + len > table.Length)
                throw new InvalidDataException("TVFS: path entry name overflows.");
            name = table.Slice(1, len).ToArray();
            table = table[(1 + len)..];
        }

        // Trailing path separator (0x00).
        if (!table.IsEmpty && table[0] == 0x00)
        {
            nodeFlags |= PteSeparatorPost;
            table = table[1..];
        }

        // Node value (0xFF marker + 4-byte big-endian value), or an implicit post-separator.
        if (!table.IsEmpty)
        {
            if (table[0] == 0xFF)
            {
                if (table.Length < 5)
                    throw new InvalidDataException("TVFS: path entry node value truncated.");
                nodeValue = ReadU32BE(table.Slice(1, 4));
                nodeFlags |= PteNodeValue;
                table = table[5..];
            }
            else
            {
                nodeFlags |= PteSeparatorPost;
            }
        }

        return table;
    }

    // ── Binary helpers ─────────────────────────────────────────────────────────

    private static uint ReadU32BE(ReadOnlySpan<byte> b) =>
        ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

    private static long ReadIntBE(ReadOnlySpan<byte> b, int n)
    {
        long v = 0;
        for (int i = 0; i < n; i++) v = (v << 8) | b[i];
        return v;
    }

    private static int OffsetFieldSize(int size)
    {
        if (size > 0xFFFFFF) return 4;
        if (size > 0xFFFF) return 3;
        if (size > 0xFF) return 2;
        return 1;
    }

    private static string Key9(byte[] ekey)
    {
        // 9-byte prefix as a stable dictionary key.
        var sb = new StringBuilder(18);
        for (int i = 0; i < 9 && i < ekey.Length; i++) sb.Append(ekey[i].ToString("x2"));
        return sb.ToString();
    }
}
