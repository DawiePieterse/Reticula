namespace Reticula.Infrastructure.Maps;

/// <summary>Web Mercator tile arithmetic and PMTiles tile ids.</summary>
public static class TileMath
{
    public static int LonToX(double lon, int z) => Math.Clamp((int)Math.Floor((lon + 180.0) / 360.0 * (1 << z)), 0, (1 << z) - 1);

    public static int LatToY(double lat, int z)
    {
        var r = lat * Math.PI / 180.0;
        var y = (1.0 - Math.Log(Math.Tan(r) + 1.0 / Math.Cos(r)) / Math.PI) / 2.0 * (1 << z);
        return Math.Clamp((int)Math.Floor(y), 0, (1 << z) - 1);
    }

    public static double XToLon(int x, int z) => x / (double)(1 << z) * 360.0 - 180.0;

    public static double YToLat(int y, int z)
    {
        var n = Math.PI - 2.0 * Math.PI * y / (1 << z);
        return 180.0 / Math.PI * Math.Atan(Math.Sinh(n));
    }

    /// <summary>Tile bounds as (west, south, east, north) in degrees.</summary>
    public static (double W, double S, double E, double N) Bounds(int z, int x, int y) =>
        (XToLon(x, z), YToLat(y + 1, z), XToLon(x + 1, z), YToLat(y, z));

    /// <summary>The PMTiles v3 tile id: tiles of lower zooms first, then a Hilbert curve within the zoom.</summary>
    public static ulong ZxyToTileId(int z, int x, int y)
    {
        if (z > 31) throw new ArgumentOutOfRangeException(nameof(z));
        var n = 1UL << z;
        if ((ulong)x >= n || (ulong)y >= n) throw new ArgumentOutOfRangeException(nameof(x), "Tile is outside its zoom level.");
        ulong acc = 0;
        for (var t = 0; t < z; t++) acc += 1UL << (2 * t);

        ulong d = 0;
        ulong tx = (ulong)x, ty = (ulong)y;
        for (var s = n / 2; s > 0; s /= 2)
        {
            var rx = (tx & s) > 0 ? 1UL : 0UL;
            var ry = (ty & s) > 0 ? 1UL : 0UL;
            d += s * s * ((3 * rx) ^ ry);
            if (ry == 0)
            {
                if (rx == 1)
                {
                    tx = s - 1 - tx;
                    ty = s - 1 - ty;
                }
                (tx, ty) = (ty, tx);
            }
        }
        return acc + d;
    }
}
