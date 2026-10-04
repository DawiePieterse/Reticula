using NetTopologySuite.Geometries;

namespace Reticula.Domain.Field;

public static class CandidateKinds
{
    public const string Transformer = "transformer";
    public const string MiniSub = "minisub";
    public const string Pole = "pole";
    public const string MvRoute = "mv_route";
    public const string LvRoute = "lv_route";

    public static readonly IReadOnlyList<string> Sites = [Transformer, MiniSub, Pole];
    public static readonly IReadOnlyList<string> Routes = [MvRoute, LvRoute];
    public static readonly IReadOnlyList<string> All = [.. Sites, .. Routes];

    public static bool IsRoute(string kind) => Routes.Contains(kind);
}

/// <summary>A site or route the inspector marked as possible for the design. Sites are points, routes are lines.</summary>
public sealed class Candidate
{
    private Candidate() { } // EF

    public Candidate(Guid id, Guid projectId, string kind, Geometry geometry, string? notes, Guid createdBy, DateTimeOffset now)
    {
        Id = id;
        ProjectId = projectId;
        Kind = kind;
        Geometry = geometry;
        Notes = notes;
        CreatedBy = createdBy;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Kind { get; private set; } = "";
    public Geometry Geometry { get; private set; } = null!;
    public string? Notes { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public uint Version { get; private set; }

    public void Update(Geometry geometry, string? notes, DateTimeOffset now)
    {
        Geometry = geometry;
        Notes = notes;
        UpdatedAt = now;
    }

    public void Archive(DateTimeOffset now)
    {
        ArchivedAt = now;
        UpdatedAt = now;
    }
}
