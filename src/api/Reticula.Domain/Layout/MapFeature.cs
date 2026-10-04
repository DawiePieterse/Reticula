using NetTopologySuite.Geometries;

namespace Reticula.Domain.Layout;

/// <summary>
/// An imported map feature other than stands and buildings: a road, a contour line or an existing network asset.
/// Re-importing a layer replaces that layer's features. WGS84 geometry.
/// </summary>
public sealed class MapFeature
{
    private MapFeature() { } // EF

    public MapFeature(Guid id, Guid projectId, Guid importBatchId, string layer, string sourceRef, string? subtype, string? name,
        double? elevationM, Geometry geometry, double lengthM, string attributesJson)
    {
        Id = id;
        ProjectId = projectId;
        ImportBatchId = importBatchId;
        Layer = layer;
        SourceRef = sourceRef;
        Subtype = subtype;
        Name = name;
        ElevationM = elevationM;
        Geometry = geometry;
        LengthM = lengthM;
        AttributesJson = attributesJson;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ImportBatchId { get; private set; }

    /// <summary>One of <see cref="MapLayers"/>.</summary>
    public string Layer { get; private set; } = "";
    public string SourceRef { get; private set; } = "";

    /// <summary>Road class (OSM highway value) or network asset type (mv_line, transformer, …).</summary>
    public string? Subtype { get; private set; }
    public string? Name { get; private set; }

    /// <summary>Contour height in metres.</summary>
    public double? ElevationM { get; private set; }
    public Geometry Geometry { get; private set; } = null!;
    public double LengthM { get; private set; }

    /// <summary>Normalised fields (network: voltage_kv, conductor, rating_kva, …) plus the source fields.</summary>
    public string AttributesJson { get; private set; } = "{}";
}

public static class MapLayers
{
    public const string Roads = "roads";
    public const string Contours = "contours";
    public const string Network = "network";
    public static readonly IReadOnlyList<string> All = [Roads, Contours, Network];
}
