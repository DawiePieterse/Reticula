using System.Text.Json;
using NetTopologySuite.Geometries;

namespace Reticula.Infrastructure.Geo;

/// <summary>A GeoJSON Point, LineString or Polygon as the calc service returns it.</summary>
public sealed record GeoJsonGeometry(string Type, JsonElement Coordinates)
{
    public static implicit operator GeoJsonGeometry(PolygonDto p) => new(p.Type, JsonSerializer.SerializeToElement(p.Coordinates));

    public static GeoJsonGeometry Line(params double[][] coordinates) => new("LineString", JsonSerializer.SerializeToElement(coordinates));

    public static GeoJsonGeometry Point(double lon, double lat) => new("Point", JsonSerializer.SerializeToElement(new[] { lon, lat }));

    public bool TryToPolygon(out Polygon? polygon, out string? error)
    {
        polygon = null;
        try
        {
            return new PolygonDto(Type, Coordinates.Deserialize<double[][][]>() ?? []).TryToPolygon(out polygon, out error);
        }
        catch (JsonException)
        {
            error = "Polygon coordinates are malformed.";
            return false;
        }
    }

    /// <summary>The geometry, or null when it is not a valid Point, LineString or Polygon.</summary>
    public Geometry? ToGeometry()
    {
        if (Type == "Polygon") return TryToPolygon(out var p, out _) ? p : null;
        return new GeometryInput(Type, Coordinates).TryToGeometry(out var g, out _) ? g : null;
    }
}
