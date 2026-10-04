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
    public Polygon Footprint { get; private set; } = null!;
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
    public DateTimeOffset UpdatedAt { get; private set; }

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
