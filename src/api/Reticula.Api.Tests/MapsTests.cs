using NetTopologySuite.Geometries;
using Reticula.Infrastructure.Maps;

namespace Reticula.Api.Tests;

public class MapsTests
{
    [Theory]
    // Values from the reference implementation (pmtiles 4.5.0, zxyToTileId).
    [InlineData(0, 0, 0, 0UL)]
    [InlineData(1, 0, 0, 1UL)]
    [InlineData(1, 0, 1, 2UL)]
    [InlineData(1, 1, 1, 3UL)]
    [InlineData(1, 1, 0, 4UL)]
    [InlineData(2, 1, 3, 11UL)]
    [InlineData(12, 2367, 2337, 14115155UL)]
    [InlineData(18, 151754, 149608, 57835232123UL)]
    [InlineData(18, 151755, 149612, 57835232138UL)]
    public void Tile_ids_match_the_pmtiles_reference(int z, int x, int y, ulong expected) =>
        Assert.Equal(expected, TileMath.ZxyToTileId(z, x, y));

    [Fact]
    public void Lon_lat_map_to_the_tile_that_contains_them()
    {
        var (x, y) = (TileMath.LonToX(28.1003, 16), TileMath.LatToY(-25.5195, 16));
        var (w, s, e, n) = TileMath.Bounds(16, x, y);
        Assert.InRange(28.1003, w, e);
        Assert.InRange(-25.5195, s, n);
    }

    private static byte[] Png(int seed) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, (byte)seed, (byte)(seed >> 8), (byte)(seed >> 16)];

    [Fact]
    public void Archive_round_trips_with_shared_tiles_stored_once()
    {
        var sea = Png(0);
        var tiles = new List<(int, int, int, byte[])> { (14, 9471, 9349, Png(1)), (14, 9472, 9349, sea), (14, 9472, 9350, sea), (15, 18943, 18699, Png(2)) };
        // Fill a run of consecutive tile ids with the same content.
        for (var i = 0; i < 4; i++) tiles.Add((13, 4735 + i % 2, 4674 + i / 2, sea));
        using var ms = new MemoryStream();
        var n = PmTilesWriter.Write(ms, tiles, new PmTilesInfo(PmTileType.Png, 13, 15, 28.09, -25.53, 28.12, -25.50, "{\"name\":\"t\"}"));

        var r = new PmTilesReader(ms.ToArray());
        Assert.Equal(("PMTiles", 3), (r.Magic, r.SpecVersion));
        Assert.Equal(8, n);
        Assert.Equal(8UL, r.AddressedTiles);
        Assert.Equal(3UL, r.TileContents);
        Assert.True(r.TileEntries < 8, "consecutive identical tiles share an entry");
        Assert.Equal((2, 13, 15), (r.TileType, r.MinZoom, r.MaxZoom));
        Assert.Equal(28.09, r.West, 6);
        Assert.Equal(-25.50, r.North, 6);
        Assert.Contains("\"name\":\"t\"", r.Metadata);
        foreach (var (z, x, y, data) in tiles) Assert.Equal(data, r.Tile(z, x, y));
        Assert.Null(r.Tile(14, 1, 1));
    }

    [Fact]
    public void Large_archives_use_leaf_directories_and_still_read_back()
    {
        // Scattered tiles of varied sizes make a directory too big for the first 16 KiB.
        var rnd = new Random(1);
        var tiles = new List<(int, int, int, byte[])>();
        for (var x = 0; x < 256; x++)
        for (var y = 0; y < 256; y++)
            if (rnd.Next(2) == 0) tiles.Add((16, 37000 + x, 37000 + y, [.. Png(tiles.Count + 1), .. new byte[rnd.Next(1, 3000)]]));
        using var ms = new MemoryStream();
        PmTilesWriter.Write(ms, tiles, new PmTilesInfo(PmTileType.Png, 16, 16, 0, 0, 1, 1, "{}"));
        var r = new PmTilesReader(ms.ToArray());
        Assert.True(r.LeafLength > 0, "leaf directories expected");
        Assert.True(r.RootOffset + r.RootLength <= 16384, "root directory must sit in the first 16 KiB");
        foreach (var t in tiles.Where((_, k) => k % 997 == 0)) Assert.Equal(t.Item4, r.Tile(t.Item1, t.Item2, t.Item3));
    }

    [Fact]
    public void Duplicate_tiles_are_rejected()
    {
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentException>(() => PmTilesWriter.Write(ms, [(1, 0, 0, Png(1)), (1, 0, 0, Png(2))], new PmTilesInfo(PmTileType.Png, 1, 1, 0, 0, 1, 1, "{}")));
    }

    private static Polygon Area(double lon, double lat, double d) => new GeometryFactory(new PrecisionModel(), 4326).CreatePolygon(
        [new(lon, lat), new(lon + d, lat), new(lon + d, lat + d), new(lon, lat + d), new(lon, lat)]);

    [Fact]
    public void Plan_covers_the_area_with_a_margin_and_stops_at_the_tile_budget()
    {
        var area = Area(28.09, -25.53, 0.03);
        var generous = TilePackPlanner.Plan(area, new TileOptions("http://t/{z}/{x}/{y}.png", "a", 12, 18, 100_000, 0.5, 4));
        Assert.Equal((12, 18), (generous.MinZoom, generous.MaxZoom));
        Assert.True(generous.West < 28.09 && generous.East > 28.12 && generous.South < -25.53 && generous.North > -25.50);
        Assert.Equal(generous.TileCount, generous.Tiles.Distinct().Count());

        var tight = TilePackPlanner.Plan(area, new TileOptions("http://t/{z}/{x}/{y}.png", "a", 12, 18, 500, 0.5, 4));
        Assert.True(tight.MaxZoom < 18);
        Assert.True(tight.TileCount <= 500);
        Assert.All(tight.Tiles, t => Assert.InRange(t.Z, 12, tight.MaxZoom));

        Assert.Throws<InvalidOperationException>(() => TilePackPlanner.Plan(area, new TileOptions("x", "a", 18, 18, 10, 0.5, 4)));
    }
}
