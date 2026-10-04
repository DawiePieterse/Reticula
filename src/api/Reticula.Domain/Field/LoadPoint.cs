namespace Reticula.Domain.Field;

public enum LoadPointStatus
{
    /// <summary>Estimated from observations; stays in the assumptions register.</summary>
    Estimated,
    /// <summary>Confirmed by the engineer; its assumptions are cleared.</summary>
    Confirmed,
}

public static class LoadKinds
{
    public const string Residential = "residential";
    public const string Special = "special";
}

/// <summary>
/// The load at one building: what was observed, the income band and ADMD the calc service derived,
/// and any override. Records are kept per load point, never per person.
/// </summary>
public sealed class LoadPoint
{
    private LoadPoint() { } // EF

    public LoadPoint(Guid id, Guid projectId, Guid buildingId)
    {
        Id = id;
        ProjectId = projectId;
        BuildingId = buildingId;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid BuildingId { get; private set; }
    public string Kind { get; private set; } = LoadKinds.Residential;
    public string? SpecialLoad { get; private set; }
    public string ObservationsJson { get; private set; } = "{}";
    public string? IncomeBand { get; private set; }

    /// <summary>Load class the engineer chose; null when the class came from the indicator score.</summary>
    public string? ClassOverride { get; private set; }
    public string? Category { get; private set; }

    /// <summary>ADMD (or special-load kVA) the method gives.</summary>
    public double EstimatedKva { get; private set; }

    /// <summary>ADMD used for design: the override when there is one, else the estimate.</summary>
    public double Kva { get; private set; }

    public bool Overridden { get; private set; }
    public string? OverrideReason { get; private set; }
    public string MissingJson { get; private set; } = "[]";
    public string TraceJson { get; private set; } = "{}";
    public string RulesHash { get; private set; } = "";
    public LoadPointStatus Status { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid? ConfirmedBy { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public uint Version { get; private set; }

    /// <summary>1 for a single-phase domestic connection, 3 for a three-phase one.</summary>
    public int Phases { get; private set; } = 1;

    public void SetPhases(int phases) => Phases = phases == 3 ? 3 : 1;

    public void SetEstimate(string kind, string? specialLoad, string observationsJson, string? classOverride, string? incomeBand, string? category,
        double estimatedKva, double kva, bool overridden, string? overrideReason, string missingJson, string traceJson,
        string rulesHash, Guid by, DateTimeOffset now)
    {
        Kind = kind;
        SpecialLoad = specialLoad;
        ObservationsJson = observationsJson;
        ClassOverride = classOverride;
        IncomeBand = incomeBand;
        Category = category;
        EstimatedKva = estimatedKva;
        Kva = kva;
        Overridden = overridden;
        OverrideReason = overrideReason;
        MissingJson = missingJson;
        TraceJson = traceJson;
        RulesHash = rulesHash;
        Status = LoadPointStatus.Estimated;
        ConfirmedBy = null;
        ConfirmedAt = null;
        UpdatedBy = by;
        UpdatedAt = now;
    }

    public void Confirm(Guid by, DateTimeOffset now)
    {
        Status = LoadPointStatus.Confirmed;
        ConfirmedBy = by;
        ConfirmedAt = now;
        UpdatedAt = now;
    }
}
