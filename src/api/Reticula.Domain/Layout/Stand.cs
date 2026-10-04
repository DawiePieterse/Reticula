using NetTopologySuite.Geometries;

namespace Reticula.Domain.Layout;

/// <summary>A stand (erf) from a planner layout or map data. WGS84 geometry.</summary>
public sealed class Stand
{
    private Stand() { } // EF

    public Stand(Guid id, Guid projectId, Guid importBatchId, string sourceRef, string? erfNumber, string? zoning,
        Polygon geometry, double areaM2, string attributesJson)
    {
        Id = id;
        ProjectId = projectId;
        ImportBatchId = importBatchId;
        SourceRef = sourceRef;
        ErfNumber = erfNumber;
        Zoning = zoning;
        Geometry = geometry;
        AreaM2 = areaM2;
        AttributesJson = attributesJson;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ImportBatchId { get; private set; }

    /// <summary>Identifier in the source file (placemark, polyline handle, feature id).</summary>
    public string SourceRef { get; private set; } = "";

    public string? ErfNumber { get; private set; }
    public string? Zoning { get; private set; }
    public Polygon Geometry { get; private set; } = null!;

    /// <summary>Geodesic area, computed by the calc service.</summary>
    public double AreaM2 { get; private set; }

    public string AttributesJson { get; private set; } = "{}";
}
