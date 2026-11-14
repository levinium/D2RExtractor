using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace D2RExtractor.Services.Steam;

/// <summary>
/// Decoder for Blizzard's BLTE block-container format used inside CASC storages.
///
/// A BLTE blob is an 8-byte header (magic 'BLTE' + big-endian header size),
/// an optional block table, then one or more blocks. Each block is prefixed
/// with a one-byte encoding mode:
///   'N' (0x4E) — raw / not compressed
///   'Z' (0x5A) — zlib/deflate compressed
///   'E' (0x45) — encrypted (Salsa20); D2R game assets are not encrypted, so an
///                encrypted block is treated as unreadable rather than supported.
///   'F' (0x46) — recursive BLTE frame; not used by D2R.
///
/// This is a self-contained port of the block-decoding logic; it does not depend
/// on CascLib.dll. Ported from the reference implementation in rustydemon
/// (rustydemon-lib/src/blte.rs).
/// </summary>
internal static class Blte
{
    private const uint BlteMagic = 0x45544C42; // 'BLTE' little-endian

    /// <summary>Thrown when a BLTE blob cannot be decoded (bad structure or an unsupported/encrypted block).</summary>
    internal sealed class BlteException : Exception
    {
        public BlteException(string message) : base(message) { }
    }

    private readonly struct BlockDesc
    {
        public readonly int CompSize;
        public readonly int DecompSize;
        public BlockDesc(int compSize, int decompSize) { CompSize = compSize; DecompSize = decompSize; }
    }

    /// <summary>
    /// Decodes a complete BLTE blob (header + blocks) into raw file bytes.
    /// </summary>
    /// <param name="data">The full BLTE-encoded blob.</param>
    /// <returns>The decoded logical file bytes.</returns>
    internal static byte[] Decode(byte[] data)
    {
        if (data.Length < 8)
            throw new BlteException("BLTE blob too short for header.");

        uint magic = BitConverter.ToUInt32(data, 0);
        if (magic != BlteMagic)
            throw new BlteException($"Invalid BLTE magic 0x{magic:X8}.");

        int headerSize = ReadInt32BE(data, 4);
        bool hasHeader = headerSize > 0;

        List<BlockDesc> blocks = hasHeader ? ParseHeader(data, headerSize) : SingleImplicitBlock(data);

        long totalDecomp = 0;
        foreach (var b in blocks) totalDecomp += b.DecompSize;

        using var outStream = new MemoryStream(totalDecomp > 0 && totalDecomp < int.MaxValue ? (int)totalDecomp : 0);

        int pos = Math.Max(headerSize, 8); // block data starts after the header
        for (int i = 0; i < blocks.Count; i++)
        {
            int comp = blocks[i].CompSize;
            int end = pos + comp;
            if (end > data.Length)
                throw new BlteException($"BLTE block {i}: compressed range {pos}..{end} exceeds blob length {data.Length}.");

            DecodeBlock(data, pos, comp, i, outStream);
            pos = end;
        }

        return outStream.ToArray();
    }

    private static List<BlockDesc> SingleImplicitBlock(byte[] data)
    {
        // Headerless BLTE: everything after the 8-byte header is one block.
        int payload = data.Length - 8;
        return new List<BlockDesc> { new BlockDesc(payload, Math.Max(payload - 1, 0)) };
    }

    private static List<BlockDesc> ParseHeader(byte[] data, int headerSize)
    {
        if (data.Length < 12)
            throw new BlteException("BLTE header too short.");

        byte flag = data[8];
        if (flag != 0x0F)
            throw new BlteException($"Unexpected BLTE frame-count flag 0x{flag:X2} (expected 0x0F).");

        int numBlocks = (data[9] << 16) | (data[10] << 8) | data[11];
        if (numBlocks == 0)
            throw new BlteException("BLTE block count is zero.");

        int expected = 12 + numBlocks * 24;
        if (headerSize != expected)
            throw new BlteException($"BLTE header size {headerSize} != expected {expected}.");
        if (data.Length < expected)
            throw new BlteException("BLTE blob truncated before block table.");

        var blocks = new List<BlockDesc>(numBlocks);
        int off = 12;
        for (int i = 0; i < numBlocks; i++)
        {
            int compSize = ReadInt32BE(data, off);
            int decompSize = ReadInt32BE(data, off + 4);
            // 16-byte per-block MD5 at off+8 is skipped (integrity check not needed for extraction).
            blocks.Add(new BlockDesc(compSize, decompSize));
            off += 24;
        }
        return blocks;
    }

    private static void DecodeBlock(byte[] data, int start, int length, int blockIdx, Stream outStream)
    {
        if (length == 0)
            throw new BlteException($"BLTE block {blockIdx} is empty.");

        byte mode = data[start];
        int payloadStart = start + 1;
        int payloadLen = length - 1;

        switch (mode)
        {
            case 0x4E: // 'N' — raw
                outStream.Write(data, payloadStart, payloadLen);
                break;

            case 0x5A: // 'Z' — zlib
            {
                using var src = new MemoryStream(data, payloadStart, payloadLen, writable: false);
                using var z = new ZLibStream(src, CompressionMode.Decompress);
                z.CopyTo(outStream);
                break;
            }

            case 0x45: // 'E' — encrypted
                throw new BlteException(
                    $"BLTE block {blockIdx} is encrypted ('E'); D2R game assets are not expected to be encrypted.");

            case 0x46: // 'F' — recursive frame
                throw new BlteException($"BLTE block {blockIdx} uses recursive frames ('F'), which are not supported.");

            default:
                throw new BlteException($"BLTE block {blockIdx}: unknown block mode 0x{mode:X2}.");
        }
    }

    private static int ReadInt32BE(byte[] b, int i) =>
        (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
}
