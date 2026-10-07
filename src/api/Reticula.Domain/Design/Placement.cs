namespace Reticula.Domain.Design;

/// <summary>The pre-design proposal for a project (ADR 0010): transformer sites, the loads each feeds, LV and MV routes, as the
/// calc service returned them. The proposed sites and routes themselves are stored as candidates with source "proposed".</summary>
public sealed class Placement
{
    private Placement() { } // EF

    public Placement(Guid id, Guid projectId, string rulesRef, string rulesHash, string clause, string resultJson, int transformers, int loads,
        int unplaced, Guid builtBy, DateTimeOffset builtAt)
    {
        Id = id;
        ProjectId = projectId;
        RulesRef = rulesRef;
        RulesHash = rulesHash;
        Clause = clause;
        ResultJson = resultJson;
        Transformers = transformers;
        Loads = loads;
        Unplaced = unplaced;
        BuiltBy = builtBy;
        BuiltAt = builtAt;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string RulesRef { get; private set; } = "";
    public string RulesHash { get; private set; } = "";
    public string Clause { get; private set; } = "";
    /// <summary>The calc service's full result: transformers, assignments, routes, traced cost, issues and placeholders.</summary>
    public string ResultJson { get; private set; } = "{}";
    public int Transformers { get; private set; }
    public int Loads { get; private set; }
    /// <summary>Loads the proposal could not feed: far from any road, or beyond reach of every site.</summary>
    public int Unplaced { get; private set; }
    public Guid BuiltBy { get; private set; }
    public DateTimeOffset BuiltAt { get; private set; }
}
