using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Reticula.Infrastructure.Maps;

namespace Reticula.Api.Tests;

/// <summary>A minimal PMTiles v3 reader for checking archives the API writes (gzip directories only).</summary>
public sealed class PmTilesReader(byte[] archive)
{
    private sealed record Entry(ulong TileId, ulong Offset, ulong Length, ulong RunLength);

    public string Magic => Encoding.ASCII.GetString(archive, 0, 7);
    public int SpecVersion => archive[7];
    private ulong U64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(archive.AsSpan(at));
    public ulong RootOffset => U64(8);
    public ulong RootLength => U64(16);
    public ulong AddressedTiles => U64(72);
    public ulong TileEntries => U64(80);
    public ulong TileContents => U64(88);
    public int TileType => archive[99];
    public int MinZoom => archive[100];
    public int MaxZoom => archive[101];
    public double West => BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(102)) / 1e7;
    public double North => BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(114)) / 1e7;
    public ulong LeafLength => U64(48);

    public string Metadata => Encoding.UTF8.GetString(Gunzip(archive.AsSpan((int)U64(24), (int)U64(32)).ToArray()));

    public byte[]? Tile(int z, int x, int y)
    {
        var id = TileMath.ZxyToTileId(z, x, y);
        var (offset, length) = (RootOffset, RootLength);
        for (var depth = 0; depth < 4; depth++)
        {
            var dir = Directory(archive.AsSpan((int)offset, (int)length).ToArray());
            var e = Find(dir, id);
            if (e is null) return null;
            if (e.RunLength > 0)
                return archive.AsSpan((int)(U64(56) + e.Offset), (int)e.Length).ToArray();
            (offset, length) = (U64(40) + e.Offset, e.Length);
        }
        return null;
    }

    private static Entry? Find(List<Entry> dir, ulong id)
    {
        Entry? best = null;
        foreach (var e in dir)
        {
            if (e.TileId > id) break;
            best = e;
        }
        if (best is null) return null;
        if (best.RunLength == 0) return best; // leaf pointer
        return id < best.TileId + best.RunLength ? best : null;
    }

    private static List<Entry> Directory(byte[] gz)
    {
        var b = Gunzip(gz);
        var pos = 0;
        ulong Read()
        {
            ulong v = 0;
            var shift = 0;
            while (true)
            {
                var c = b[pos++];
                v |= (ulong)(c & 0x7F) << shift;
                if (c < 0x80) return v;
                shift += 7;
            }
        }
        var n = (int)Read();
        var ids = new ulong[n];
        var runs = new ulong[n];
        var lens = new ulong[n];
        var offs = new ulong[n];
        ulong last = 0;
        for (var i = 0; i < n; i++) ids[i] = last += Read();
        for (var i = 0; i < n; i++) runs[i] = Read();
        for (var i = 0; i < n; i++) lens[i] = Read();
        for (var i = 0; i < n; i++)
        {
            var v = Read();
            offs[i] = v == 0 && i > 0 ? offs[i - 1] + lens[i - 1] : v - 1;
        }
        return [.. Enumerable.Range(0, n).Select(i => new Entry(ids[i], offs[i], lens[i], runs[i]))];
    }

    private static byte[] Gunzip(byte[] gz)
    {
        using var input = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return ms.ToArray();
    }
}
