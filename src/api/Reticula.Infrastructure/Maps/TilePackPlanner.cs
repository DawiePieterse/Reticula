using Microsoft.Extensions.Configuration;
using NetTopologySuite.Geometries;

namespace Reticula.Infrastructure.Maps;

/// <summary>
/// Settings for offline base maps (section <c>Tiles</c>). The source must be a raster XYZ tile server whose terms allow
/// downloading tiles for offline use; the public OpenStreetMap servers do not, so there is no default.
/// </summary>
public sealed record TileOptions(string? SourceUrl, string Attribution, int MinZoom, int MaxZoom, int MaxTiles, double MarginKm, int Concurrency)
{
    public static TileOptions From(IConfiguration config)
    {
        var s = config.GetSection("Tiles");
        return new TileOptions(
            s["SourceUrl"] is { Length: > 0 } u ? u : null,
            s["Attribution"] ?? "© OpenStreetMap contributors",
            s.GetValue("MinZoom", 10),
            s.GetValue("MaxZoom", 18),
            s.GetValue("MaxTiles", 30000),
            s.GetValue("MarginKm", 0.5),
            s.GetValue("Concurrency", 4));
    }

    /// <summary>The source for display: scheme, host and path, without query strings that may carry keys.</summary>
    public string SourceLabel => SourceUrl is null ? "" : SourceUrl.Split('?')[0];
}

public sealed record TilePlan(int MinZoom, int MaxZoom, int TileCount, double West, double South, double East, double North, IReadOnlyList<(int Z, int X, int Y)> Tiles);

/// <summary>Chooses the tiles for a project area: the area plus a margin, as deep as the tile budget allows.</summary>
public static class TilePackPlanner
{
    public static TilePlan Plan(Polygon area, TileOptions o)
    {
        // About 0.009° per km of latitude; widen longitude by 1/cos(lat).
        var env = area.EnvelopeInternal;
        var lat = (env.MinY + env.MaxY) / 2;
        var dLat = o.MarginKm / 111.32;
        var dLon = dLat / Math.Max(Math.Cos(lat * Math.PI / 180), 0.1);
        var cover = area.Buffer(Math.Max(dLat, dLon));
        var b = cover.EnvelopeInternal;

        var tiles = new List<(int, int, int)>();
        var maxZoom = o.MinZoom - 1;
        for (var z = o.MinZoom; z <= o.MaxZoom; z++)
        {
            var level = TilesAt(cover, b, z);
            if (tiles.Count + level.Count > o.MaxTiles) break;
            tiles.AddRange(level);
            maxZoom = z;
        }
        if (maxZoom < o.MinZoom)
            throw new InvalidOperationException($"The project area needs more than {o.MaxTiles} tiles even at zoom {o.MinZoom}.");
        return new TilePlan(o.MinZoom, maxZoom, tiles.Count, b.MinX, b.MinY, b.MaxX, b.MaxY, tiles);
    }

    private static List<(int, int, int)> TilesAt(Geometry cover, Envelope b, int z)
    {
        var x0 = TileMath.LonToX(b.MinX, z);
        var x1 = TileMath.LonToX(b.MaxX, z);
        var y0 = TileMath.LatToY(b.MaxY, z);
        var y1 = TileMath.LatToY(b.MinY, z);
        var f = cover.Factory;
        var list = new List<(int, int, int)>();
        for (var x = x0; x <= x1; x++)
        for (var y = y0; y <= y1; y++)
        {
            var (w, s, e, n) = TileMath.Bounds(z, x, y);
            if (cover.Intersects(f.ToGeometry(new Envelope(w, e, s, n)))) list.Add((z, x, y));
        }
        return list;
    }
}
