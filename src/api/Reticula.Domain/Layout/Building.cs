using NetTopologySuite.Geometries;

namespace Reticula.Domain.Layout;

public enum BuildingStatus
{
    /// <summary>Type predicted from map data; not yet seen on site.</summary>
    Predicted,
    /// <summary>Inspector confirmed or corrected the type on site.</summary>
    Confirmed,
    /// <summary>Inspector found no building here.</summary>
    NotPresent,
    /// <summary>Inspector added a building missing from the map data.</summary>
    New,
}

public static class BuildingTypes
{
    public const string House = "house";
    public const string Shop = "shop";
    public const string School = "school";
    public const string Other = "other";
    public static readonly IReadOnlyList<string> All = [House, Shop, School, Other];
}

/// <summary>A building footprint with its predicted type and, after inspection, the confirmed type.</summary>
public sealed class Building
{
    private Building() { } // EF

    public Building(Guid id, Guid projectId, Guid? importBatchId, string sourceRef, string? osmId,
        Polygon footprint, double areaM2, string tagsJson, DateTimeOffset now)
    {
        Id = id;
        ProjectId = projectId;
        ImportBatchId = importBatchId;
        SourceRef = sourceRef;
        OsmId = osmId;
        Footprint = footprint;
        Location = footprint.Centroid;
        Location.SRID = footprint.SRID;
        AreaM2 = areaM2;
        TagsJson = tagsJson;
        Status = BuildingStatus.Predicted;
        PredictedType = BuildingTypes.Other;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid? ImportBatchId { get; private set; }
    public string SourceRef { get; private set; } = "";
    public string? OsmId { get; private set; }
    /// <summary>Outline from map data. Null for buildings added in the field, which only have a location.</summary>
    public Polygon? Footprint { get; private set; }

    /// <summary>Representative point: footprint centroid, or the GPS position for a building added in the field.</summary>
    public Point Location { get; private set; } = null!;
    public double AreaM2 { get; private set; }
    public string TagsJson { get; private set; } = "{}";

    /// <summary>Stand the building mostly sits on, and that stand's zoning.</summary>
    public Guid? StandId { get; private set; }
    public string? Zoning { get; private set; }

    public string PredictedType { get; private set; } = BuildingTypes.Other;
    public double PredictedConfidence { get; private set; }
    public string PredictionSource { get; private set; } = "";
    public bool LowConfidence { get; private set; } = true;
    public string PredictionSignalsJson { get; private set; } = "[]";
    public string? PredictionRulesHash { get; private set; }

    public BuildingStatus Status { get; private set; }
    public string? ConfirmedType { get; private set; }
    public Guid? InspectedBy { get; private set; }
    public DateTimeOffset? InspectedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token (Postgres xmin).</summary>
    public uint Version { get; private set; }

    /// <summary>The type to design with: the confirmed type once inspected, otherwise the prediction.</summary>
    public string EffectiveType => ConfirmedType ?? PredictedType;

    /// <summary>A building found on site that the map data did not have.</summary>
    public static Building CreateNew(Guid id, Guid projectId, Point location, string type, Guid inspectedBy, DateTimeOffset now) => new()
    {
        Id = id,
        ProjectId = projectId,
        SourceRef = "field",
        Location = location,
        TagsJson = "{}",
        Status = BuildingStatus.New,
        PredictedType = type,
        PredictionSource = "field",
        LowConfidence = false,
        PredictedConfidence = 1,
        ConfirmedType = type,
        InspectedBy = inspectedBy,
        InspectedAt = now,
        UpdatedAt = now,
    };

    /// <summary>Inspector confirmed the building and its type (the predicted type, or a correction).</summary>
    public void Confirm(string type, Guid inspectedBy, DateTimeOffset now)
    {
        if (Status != BuildingStatus.New) Status = BuildingStatus.Confirmed;
        ConfirmedType = type;
        InspectedBy = inspectedBy;
        InspectedAt = now;
        UpdatedAt = now;
    }

    public void MarkNotPresent(Guid inspectedBy, DateTimeOffset now)
    {
        Status = BuildingStatus.NotPresent;
        ConfirmedType = null;
        InspectedBy = inspectedBy;
        InspectedAt = now;
        UpdatedAt = now;
    }

    /// <summary>The rooftop classifier's signal (plan 1.3), fed into the prediction with the other signals; null when none.</summary>
    public string? RooftopSignalJson { get; private set; }

    public void SetRooftopSignal(string? signalJson) => RooftopSignalJson = signalJson;

    public void SetPrediction(string type, double confidence, string source, bool lowConfidence, string signalsJson, string rulesHash, DateTimeOffset now)
    {
        PredictedType = type;
        PredictedConfidence = confidence;
        PredictionSource = source;
        LowConfidence = lowConfidence;
        PredictionSignalsJson = signalsJson;
        PredictionRulesHash = rulesHash;
        UpdatedAt = now;
    }
}
