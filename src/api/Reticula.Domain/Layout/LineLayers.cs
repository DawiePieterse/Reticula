using NetTopologySuite.Geometries;

namespace Reticula.Domain.Layout;

/// <summary>A road or street from a planner layout or OpenStreetMap: a route the network can follow.</summary>
public sealed class Road
{
    private Road() { } // EF

    public Road(Guid id, Guid projectId, Guid importBatchId, string sourceRef, string? osmId, string? name, string? roadClass,
        LineString geometry, double lengthM, string attributesJson)
    {
        Id = id;
        ProjectId = projectId;
        ImportBatchId = importBatchId;
        SourceRef = sourceRef;
        OsmId = osmId;
        Name = name;
        RoadClass = roadClass;
        Geometry = geometry;
        LengthM = lengthM;
        AttributesJson = attributesJson;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ImportBatchId { get; private set; }
    public string SourceRef { get; private set; } = "";
    public string? OsmId { get; private set; }
    public string? Name { get; private set; }

    /// <summary>OSM highway value or the planner's class, e.g. residential, tertiary.</summary>
    public string? RoadClass { get; private set; }
    public LineString Geometry { get; private set; } = null!;
    public double LengthM { get; private set; }
    public string AttributesJson { get; private set; } = "{}";
}

/// <summary>A contour line from a topographic survey, for routes, spans and gradients.</summary>
public sealed class Contour
{
    private Contour() { } // EF

    public Contour(Guid id, Guid projectId, Guid importBatchId, string sourceRef, double elevationM, LineString geometry)
    {
        Id = id;
        ProjectId = projectId;
        ImportBatchId = importBatchId;
        SourceRef = sourceRef;
        ElevationM = elevationM;
        Geometry = geometry;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ImportBatchId { get; private set; }
    public string SourceRef { get; private set; } = "";
    public double ElevationM { get; private set; }
    public LineString Geometry { get; private set; } = null!;
}

public static class NetworkAssetTypes
{
    public const string ConnectionPoint = "connection_point";
    public static readonly IReadOnlyList<string> Points = ["connection_point", "substation", "minisub", "transformer", "switchgear", "pole", "other"];
    public static readonly IReadOnlyList<string> Lines = ["mv_line", "lv_line", "mv_cable", "lv_cable", "other_line"];
}

/// <summary>
/// An asset of the authority's existing network: a point (transformer, pole, connection point) or a line (MV or LV
/// line or cable). Capacity and fault level at a connection point come only from the authority, never from the app.
/// </summary>
public sealed class NetworkAsset
{
    private NetworkAsset() { } // EF

    public NetworkAsset(Guid id, Guid projectId, Guid importBatchId, string sourceRef, string assetType, string? label,
        Geometry geometry, double? voltageKv, double? ratingKva, double? capacityKva, double? faultLevelKa, string missingJson, string attributesJson)
    {
        Id = id;
        ProjectId = projectId;
        ImportBatchId = importBatchId;
        SourceRef = sourceRef;
        AssetType = assetType;
        Label = label;
        Geometry = geometry;
        VoltageKv = voltageKv;
        RatingKva = ratingKva;
        CapacityKva = capacityKva;
        FaultLevelKa = faultLevelKa;
        MissingJson = missingJson;
        AttributesJson = attributesJson;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ImportBatchId { get; private set; }
    public string SourceRef { get; private set; } = "";

    /// <summary>One of <see cref="NetworkAssetTypes"/>.</summary>
    public string AssetType { get; private set; } = "";
    public string? Label { get; private set; }

    /// <summary>Point or LineString, WGS84.</summary>
    public Geometry Geometry { get; private set; } = null!;
    public double? VoltageKv { get; private set; }
    public double? RatingKva { get; private set; }
    public double? CapacityKva { get; private set; }
    public double? FaultLevelKa { get; private set; }

    /// <summary>Fields the asset's type needs that the authority's file did not give.</summary>
    public string MissingJson { get; private set; } = "[]";
    public string AttributesJson { get; private set; } = "{}";
}
