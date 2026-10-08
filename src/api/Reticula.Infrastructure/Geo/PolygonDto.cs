using NetTopologySuite;
using NetTopologySuite.Geometries;
using Reticula.Domain.Projects;

namespace Reticula.Infrastructure.Geo;

/// <summary>GeoJSON Polygon: coordinates[ring][position][lon, lat]. First ring is the exterior.</summary>
public sealed record PolygonDto(string Type, double[][][] Coordinates)
{
    public static readonly GeometryFactory Factory = NtsGeometryServices.Instance.CreateGeometryFactory(ProjectRules.Srid);

    public static PolygonDto From(Polygon p) =>
        new("Polygon", [.. new[] { p.ExteriorRing }.Concat(p.InteriorRings)
            .Select(r => r.Coordinates.Select(c => new[] { c.X, c.Y }).ToArray())]);

    public bool TryToPolygon(out Polygon? polygon, out string? error)
    {
        polygon = null;
        error = null;
        if (Type != "Polygon")
        {
            error = "Area must be a GeoJSON Polygon.";
            return false;
        }
        if (Coordinates is not { Length: > 0 })
        {
            error = "Area has no coordinates.";
            return false;
        }

        var rings = new List<LinearRing>(Coordinates.Length);
        foreach (var ring in Coordinates)
        {
            if (ring is not { Length: >= 4 })
            {
                error = "Each ring needs at least 4 positions (3 corners plus the closing position).";
                return false;
            }
            if (ring.Any(pos => pos is not { Length: >= 2 } || !double.IsFinite(pos[0]) || !double.IsFinite(pos[1])))
            {
                error = "Each position must be [longitude, latitude] with finite numbers.";
                return false;
            }
            var coords = ring.Select(pos => new Coordinate(pos[0], pos[1])).ToArray();
            if (!coords[0].Equals2D(coords[^1]))
            {
                error = "Each ring must be closed: the first and last positions must be equal.";
                return false;
            }
            rings.Add(Factory.CreateLinearRing(coords));
        }

        polygon = Factory.CreatePolygon(rings[0], [.. rings.Skip(1)]);
        return true;
    }
}
