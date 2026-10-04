using System.Text.Json;
using NetTopologySuite.Geometries;

namespace Reticula.Infrastructure.Geo;

/// <summary>GeoJSON Point: [lon, lat].</summary>
public sealed record PointDto(string Type, double[] Coordinates)
{
    public static PointDto From(Point p) => new("Point", [p.X, p.Y]);

    public static PointDto Of(double lon, double lat) => new("Point", [lon, lat]);

    public bool TryToPoint(out Point? point, out string? error)
    {
        point = null;
        error = null;
        if (Type != "Point" || Coordinates is not { Length: >= 2 } || !double.IsFinite(Coordinates[0]) || !double.IsFinite(Coordinates[1]))
        {
            error = "Expected a GeoJSON Point [longitude, latitude].";
            return false;
        }
        point = PolygonDto.Factory.CreatePoint(new Coordinate(Coordinates[0], Coordinates[1]));
        return true;
    }
}

/// <summary>GeoJSON LineString: [[lon, lat], ...].</summary>
public sealed record LineStringDto(string Type, double[][] Coordinates)
{
    public static LineStringDto From(LineString l) => new("LineString", [.. l.Coordinates.Select(c => new[] { c.X, c.Y })]);

    public bool TryToLineString(out LineString? line, out string? error)
    {
        line = null;
        error = null;
        if (Type != "LineString" || Coordinates is not { Length: >= 2 }
            || Coordinates.Any(c => c is not { Length: >= 2 } || !double.IsFinite(c[0]) || !double.IsFinite(c[1])))
        {
            error = "Expected a GeoJSON LineString with at least two [longitude, latitude] positions.";
            return false;
        }
        line = PolygonDto.Factory.CreateLineString([.. Coordinates.Select(c => new Coordinate(c[0], c[1]))]);
        return true;
    }
}

/// <summary>Request geometry that may be a Point or a LineString.</summary>
public sealed record GeometryInput(string Type, JsonElement Coordinates)
{
    public bool TryToGeometry(out Geometry? geometry, out string? error)
    {
        geometry = null;
        error = null;
        try
        {
            switch (Type)
            {
                case "Point":
                    var p = new PointDto(Type, Coordinates.Deserialize<double[]>() ?? []);
                    if (!p.TryToPoint(out var point, out error)) return false;
                    geometry = point;
                    return true;
                case "LineString":
                    var l = new LineStringDto(Type, Coordinates.Deserialize<double[][]>() ?? []);
                    if (!l.TryToLineString(out var line, out error)) return false;
                    geometry = line;
                    return true;
                default:
                    error = "Geometry must be a Point or a LineString.";
                    return false;
            }
        }
        catch (JsonException)
        {
            error = "Geometry coordinates are malformed.";
            return false;
        }
    }

    public static object ToDto(Geometry g) => g switch
    {
        Point p => PointDto.From(p),
        LineString l => LineStringDto.From(l),
        Polygon poly => PolygonDto.From(poly),
        _ => throw new NotSupportedException(g.GeometryType),
    };
}
