using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Reticula.Infrastructure.Maps;

public enum PmTileType : byte { Unknown = 0, Mvt = 1, Png = 2, Jpeg = 3, Webp = 4, Avif = 5 }

public sealed record PmTilesInfo(
    PmTileType TileType, int MinZoom, int MaxZoom, double West, double South, double East, double North, string MetadataJson);

/// <summary>
/// Writes a PMTiles v3 archive (https://github.com/protomaps/PMTiles/blob/main/spec/v3/spec.md): one file holding every
/// tile of an area, readable with HTTP range requests or straight from a Blob on the device.
/// Directories and metadata are gzip-compressed; tiles are stored as given; identical tiles are stored once.
/// </summary>
public static class PmTilesWriter
{
    private const int HeaderLength = 127;
    private const int RootLimit = 16384 - HeaderLength;
    private const int LeafSize = 4096;

    private sealed record Entry(ulong TileId, ulong Offset, uint Length, uint RunLength);

    /// <param name="tiles">Tiles in any order; each (z, x, y) at most once.</param>
    /// <returns>Number of tiles addressed.</returns>
    public static long Write(Stream output, IEnumerable<(int Z, int X, int Y, byte[] Data)> tiles, PmTilesInfo info)
    {
        var ordered = tiles.Select(t => (Id: TileMath.ZxyToTileId(t.Z, t.X, t.Y), t.Data)).OrderBy(t => t.Id).ToList();
        for (var i = 1; i < ordered.Count; i++)
            if (ordered[i].Id == ordered[i - 1].Id) throw new ArgumentException("A tile was given twice.", nameof(tiles));

        // Tile data in tile-id order, with repeated content (sea, empty land) stored once and run-length encoded.
        using var data = new MemoryStream();
        var entries = new List<Entry>();
        var seen = new Dictionary<string, (ulong Offset, uint Length)>();
        foreach (var (id, bytes) in ordered)
        {
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            if (!seen.TryGetValue(key, out var at))
            {
                at = ((ulong)data.Position, (uint)bytes.Length);
                data.Write(bytes);
                seen[key] = at;
            }
            var last = entries.Count > 0 ? entries[^1] : null;
            if (last is not null && last.Offset == at.Offset && last.Length == at.Length && last.TileId + last.RunLength == id)
                entries[^1] = last with { RunLength = last.RunLength + 1 };
            else
                entries.Add(new Entry(id, at.Offset, at.Length, 1));
        }

        var (root, leaves) = BuildDirectories(entries);
        var metadata = Gzip(Encoding.UTF8.GetBytes(info.MetadataJson));

        var rootOffset = (ulong)HeaderLength;
        var metadataOffset = rootOffset + (ulong)root.Length;
        var leavesOffset = metadataOffset + (ulong)metadata.Length;
        var dataOffset = leavesOffset + (ulong)leaves.Length;

        var header = new byte[HeaderLength];
        var h = header.AsSpan();
        Encoding.ASCII.GetBytes("PMTiles").CopyTo(h);
        h[7] = 3;
        BinaryPrimitives.WriteUInt64LittleEndian(h[8..], rootOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(h[16..], (ulong)root.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(h[24..], metadataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(h[32..], (ulong)metadata.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(h[40..], leavesOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(h[48..], (ulong)leaves.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(h[56..], dataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(h[64..], (ulong)data.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(h[72..], (ulong)ordered.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(h[80..], (ulong)entries.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(h[88..], (ulong)seen.Count);
        h[96] = 1; // clustered: tile data is in tile-id order
        h[97] = 2; // internal compression: gzip
        h[98] = 1; // tile compression: none (images are compressed already)
        h[99] = (byte)info.TileType;
        h[100] = (byte)info.MinZoom;
        h[101] = (byte)info.MaxZoom;
        BinaryPrimitives.WriteInt32LittleEndian(h[102..], E7(info.West));
        BinaryPrimitives.WriteInt32LittleEndian(h[106..], E7(info.South));
        BinaryPrimitives.WriteInt32LittleEndian(h[110..], E7(info.East));
        BinaryPrimitives.WriteInt32LittleEndian(h[114..], E7(info.North));
        h[118] = (byte)info.MinZoom;
        BinaryPrimitives.WriteInt32LittleEndian(h[119..], E7((info.West + info.East) / 2));
        BinaryPrimitives.WriteInt32LittleEndian(h[123..], E7((info.South + info.North) / 2));

        output.Write(header);
        output.Write(root);
        output.Write(metadata);
        output.Write(leaves);
        data.Position = 0;
        data.CopyTo(output);
        return ordered.Count;
    }

    private static int E7(double deg) => (int)Math.Round(deg * 10_000_000);

    /// <summary>The root directory has to fit in the first 16 KiB; larger archives point it at leaf directories.</summary>
    private static (byte[] Root, byte[] Leaves) BuildDirectories(List<Entry> entries)
    {
        var root = Gzip(Serialise(entries));
        if (root.Length <= RootLimit) return (root, []);

        for (var leafSize = LeafSize; ; leafSize *= 2)
        {
            using var leaves = new MemoryStream();
            var rootEntries = new List<Entry>();
            for (var i = 0; i < entries.Count; i += leafSize)
            {
                var chunk = entries.GetRange(i, Math.Min(leafSize, entries.Count - i));
                var bytes = Gzip(Serialise(chunk));
                rootEntries.Add(new Entry(chunk[0].TileId, (ulong)leaves.Position, (uint)bytes.Length, 0));
                leaves.Write(bytes);
            }
            root = Gzip(Serialise(rootEntries));
            if (root.Length <= RootLimit) return (root, leaves.ToArray());
        }
    }

    private static byte[] Serialise(List<Entry> entries)
    {
        using var ms = new MemoryStream();
        Varint(ms, (ulong)entries.Count);
        ulong lastId = 0;
        foreach (var e in entries)
        {
            Varint(ms, e.TileId - lastId);
            lastId = e.TileId;
        }
        foreach (var e in entries) Varint(ms, e.RunLength);
        foreach (var e in entries) Varint(ms, e.Length);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            // 0 means "straight after the previous entry", otherwise offset + 1.
            if (i > 0 && e.Offset == entries[i - 1].Offset + entries[i - 1].Length) Varint(ms, 0);
            else Varint(ms, e.Offset + 1);
        }
        return ms.ToArray();
    }

    private static void Varint(Stream s, ulong v)
    {
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        s.WriteByte((byte)v);
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(bytes);
        return ms.ToArray();
    }
}
