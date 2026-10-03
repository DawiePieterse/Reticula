using NetTopologySuite.Geometries;

namespace Reticula.Domain.Projects;

public sealed class Project
{
    private Project() { } // EF

    public Project(Guid id, string name, string rulesRef, Polygon area, Guid createdBy, DateTimeOffset now)
    {
        Id = id;
        Name = name;
        RulesRef = rulesRef;
        Area = area;
        CreatedBy = createdBy;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";

    /// <summary>Rules file as authority/version, e.g. eskom/0.1.0.</summary>
    public string RulesRef { get; private set; } = "";

    public string Authority => RulesRef.Split('/')[0];

    /// <summary>Project area, WGS84 (SRID 4326).</summary>
    public Polygon Area { get; private set; } = null!;

    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }

    /// <summary>Optimistic concurrency token (Postgres xmin).</summary>
    public uint Version { get; private set; }

    public void Update(string name, string rulesRef, Polygon area, DateTimeOffset now)
    {
        Name = name;
        RulesRef = rulesRef;
        Area = area;
        UpdatedAt = now;
    }

    public void Archive(DateTimeOffset now)
    {
        ArchivedAt = now;
        UpdatedAt = now;
    }
}
