using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace D2RExtractor.Services.Steam;

/// <summary>
/// Parses the <c>.build.config</c> "Static Build Configuration" file used by
/// Steam builds of D2R (and Diablo IV / Overwatch). This replaces the classic
/// <c>.build.info</c> + hash-addressed <c>config\</c> tree used by Battle.net.
///
/// Relevant keys:
/// <code>
///   root            = &lt;ckey&gt;
///   vfs-root        = &lt;ckey&gt; &lt;ekey&gt;
///   vfs-root-espec  = z                      (z = raw zlib, not BLTE)
///   vfs-root-size   = &lt;contentSize&gt; &lt;encodedSize&gt;
///   vfs-1           = &lt;ckey&gt; &lt;ekey&gt;          (sub-directory VFS)
///   vfs-2           = ...
///   key-layout-index-bits = N
///   key-layout-0    = chunkBits archiveBits offsetBits flags
/// </code>
/// </summary>
internal sealed class SteamBuildConfig
{
    /// <summary>Encoding key (16 bytes) of the primary VFS root directory.</summary>
    public byte[] VfsRootEKey { get; private set; } = Array.Empty<byte>();

    /// <summary>Encoding keys of every <c>vfs-N</c> sub-directory (used to detect directory references in the tree).</summary>
    public IReadOnlyList<byte[]> VfsSubDirectoryEKeys { get; private set; } = Array.Empty<byte[]>();

    /// <summary>Value of <c>key-layout-index-bits</c>.</summary>
    public int KeyLayoutIndexBits { get; private set; }

    /// <summary>Parsed <c>key-layout-K</c> entries, indexed by K.</summary>
    public IReadOnlyDictionary<int, KeyLayout> KeyLayouts { get; private set; } = new Dictionary<int, KeyLayout>();

    /// <summary>One parsed <c>key-layout-K = chunkBits archiveBits offsetBits flags</c> row.</summary>
    public readonly struct KeyLayout
    {
        public readonly int ChunkBits;
        public readonly int ArchiveBits;
        public readonly int OffsetBits;
        /// <summary>Offset scale. 0 = byte offset; otherwise offsets are multiplied by this value (e.g. 1, 4096).</summary>
        public readonly uint Flags;

        public KeyLayout(int chunkBits, int archiveBits, int offsetBits, uint flags)
        {
            ChunkBits = chunkBits;
            ArchiveBits = archiveBits;
            OffsetBits = offsetBits;
            Flags = flags;
        }
    }

    /// <summary>Reads and parses a <c>.build.config</c> from disk.</summary>
    public static SteamBuildConfig Load(string buildConfigPath)
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in File.ReadLines(buildConfigPath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();
            kv[key] = val;
        }

        var cfg = new SteamBuildConfig();

        if (!kv.TryGetValue("vfs-root", out string? vfsRoot))
            throw new InvalidDataException(".build.config is missing 'vfs-root' — not a recognised static-container storage.");

        // vfs-root = <ckey> <ekey>; we need the EKey (second token).
        cfg.VfsRootEKey = ParseEKey(vfsRoot, "vfs-root");

        // Collect vfs-1, vfs-2, … sub-directory EKeys (skip vfs-root itself).
        var subDirs = new List<byte[]>();
        foreach (var pair in kv)
        {
            if (pair.Key.StartsWith("vfs-", StringComparison.OrdinalIgnoreCase) &&
                !pair.Key.Equals("vfs-root", StringComparison.OrdinalIgnoreCase) &&
                // vfs-N where N is a number (exclude vfs-*-espec / vfs-*-size).
                int.TryParse(pair.Key.AsSpan(4), out _))
            {
                subDirs.Add(ParseEKey(pair.Value, pair.Key));
            }
        }
        cfg.VfsSubDirectoryEKeys = subDirs;

        // key-layout-index-bits
        if (kv.TryGetValue("key-layout-index-bits", out string? idxBits) &&
            int.TryParse(idxBits.Trim(), out int ib))
        {
            cfg.KeyLayoutIndexBits = ib;
        }

        // key-layout-0, key-layout-1, …
        var layouts = new Dictionary<int, KeyLayout>();
        foreach (var pair in kv)
        {
            const string prefix = "key-layout-";
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string suffix = pair.Key[prefix.Length..];
            if (!int.TryParse(suffix, out int k)) continue; // skips "index-bits"

            string[] parts = pair.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            int chunkBits = int.Parse(parts[0], CultureInfo.InvariantCulture);
            int archiveBits = int.Parse(parts[1], CultureInfo.InvariantCulture);
            int offsetBits = int.Parse(parts[2], CultureInfo.InvariantCulture);
            uint flags = parts.Length > 3 ? uint.Parse(parts[3], CultureInfo.InvariantCulture) : 0u;
            layouts[k] = new KeyLayout(chunkBits, archiveBits, offsetBits, flags);
        }
        if (layouts.Count == 0)
            throw new InvalidDataException(".build.config has no 'key-layout-K' entries.");
        cfg.KeyLayouts = layouts;

        return cfg;
    }

    private static byte[] ParseEKey(string value, string keyName)
    {
        string[] tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // For "<ckey> <ekey>" pairs the EKey is the second token; a lone token is taken as-is.
        string hex = tokens.Length >= 2 ? tokens[1] : tokens.FirstOrDefault() ?? "";
        byte[] key = FromHex(hex);
        if (key.Length != 16)
            throw new InvalidDataException($".build.config: '{keyName}' EKey is {key.Length} bytes (expected 16).");
        return key;
    }

    internal static byte[] FromHex(string hex)
    {
        if ((hex.Length & 1) != 0)
            throw new FormatException($"Hex string has odd length: '{hex}'.");
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }
}
