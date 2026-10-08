namespace Reticula.Domain.Layout;

/// <summary>One imported file: what it was, how it was read, and what was flagged.</summary>
public sealed class ImportBatch
{
    private ImportBatch() { } // EF

    public ImportBatch(Guid id, Guid projectId, string kind, string fileName, string format, string? sourceCrs,
        string crsReason, int featureCount, string issuesJson, string sha256, Guid createdBy, DateTimeOffset now)
    {
        Id = id;
        ProjectId = projectId;
        Kind = kind;
        FileName = fileName;
        Format = format;
        SourceCrs = sourceCrs;
        CrsReason = crsReason;
        FeatureCount = featureCount;
        IssuesJson = issuesJson;
        Sha256 = sha256;
        CreatedBy = createdBy;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Kind { get; private set; } = "";
    public string FileName { get; private set; } = "";
    public string Format { get; private set; } = "";
    public string? SourceCrs { get; private set; }
    public string CrsReason { get; private set; } = "";
    public int FeatureCount { get; private set; }
    public string IssuesJson { get; private set; } = "[]";
    public string Sha256 { get; private set; } = "";
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

public static class ImportKinds
{
    public const string Stands = "stands";
    public const string Buildings = "buildings";
    public const string Roads = "roads";
    public const string Contours = "contours";
    public const string Network = "network";
    public static readonly IReadOnlyList<string> All = [Stands, Buildings, Roads, Contours, Network];

    /// <summary>Kinds that can be fetched from OpenStreetMap instead of a file.</summary>
    public static readonly IReadOnlyList<string> FromOsm = [Buildings, Roads];
}
