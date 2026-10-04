using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Design;
using Reticula.Domain.Field;
using Reticula.Infrastructure.Data;

namespace Reticula.Infrastructure.Documents;

/// <summary>A design run a document set is made from.</summary>
public sealed record SourceRun(Guid RunId, DateTimeOffset? FinishedAt);

/// <summary>
/// Everything upstream of the documents (plan 6.7): the latest finished LV design per transformer site, MV design, bulk
/// study and option search, the connection point, the rate list, the rules, and the loads and assumptions. Two sets
/// with the same hash were made from the same inputs; a different current hash makes the latest set stale.
/// </summary>
public sealed record DocumentSourceSet(
    string RulesRef,
    Dictionary<string, SourceRun> Lv,
    SourceRun? Mv,
    SourceRun? Bulk,
    SourceRun? Options,
    DateTimeOffset? ConnectionPoint,
    string RateList,
    int Loads,
    DateTimeOffset? LoadsChanged,
    int OpenAssumptions,
    DateTimeOffset? AssumptionsChanged)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())));

    public static DocumentSourceSet? FromJson(string json) => JsonSerializer.Deserialize<DocumentSourceSet>(json, Json);

    /// <summary>What differs between a set's sources and the current ones, in words.</summary>
    public IReadOnlyList<string> ChangesFrom(DocumentSourceSet then)
    {
        var out_ = new List<string>();
        if (RulesRef != then.RulesRef) out_.Add($"rules changed to {RulesRef}");
        foreach (var (site, run) in Lv)
            if (!then.Lv.TryGetValue(site, out var old) || old.RunId != run.RunId) out_.Add("a newer LV design");
        if (then.Lv.Keys.Except(Lv.Keys).Any()) out_.Add("an LV design was removed");
        if (Mv?.RunId != then.Mv?.RunId) out_.Add("a newer MV design");
        if (Bulk?.RunId != then.Bulk?.RunId) out_.Add("a newer bulk supply study");
        if (Options?.RunId != then.Options?.RunId) out_.Add("a newer option search");
        if (ConnectionPoint != then.ConnectionPoint) out_.Add("the connection point changed");
        if (RateList != then.RateList) out_.Add("the rate list changed");
        if (Loads != then.Loads || LoadsChanged != then.LoadsChanged) out_.Add("loads changed");
        if (OpenAssumptions != then.OpenAssumptions || AssumptionsChanged != then.AssumptionsChanged) out_.Add("assumptions changed");
        return [.. out_.Distinct()];
    }

    public static async Task<DocumentSourceSet> CurrentAsync(ReticulaDbContext db, Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
        var runs = await db.DesignRuns.AsNoTracking().Where(r => r.ProjectId == projectId && r.Status == DesignRunStatus.Succeeded)
            .OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        var lv = new Dictionary<string, SourceRun>();
        foreach (var r in runs.Where(r => r.Kind == DesignKinds.Lv))
        {
            using var p = JsonDocument.Parse(r.ParametersJson);
            var site = p.RootElement.TryGetProperty("transformerCandidateId", out var s) ? s.GetString() ?? "" : "";
            lv.TryAdd(site, new SourceRun(r.Id, r.FinishedAt));
        }
        SourceRun? Latest(string kind) => runs.FirstOrDefault(r => r.Kind == kind) is { } r ? new SourceRun(r.Id, r.FinishedAt) : null;
        var cp = await db.ConnectionPoints.AsNoTracking().Where(c => c.ProjectId == projectId).Select(c => (DateTimeOffset?)c.UpdatedAt).FirstOrDefaultAsync(ct);
        var rate = project.RateListId is { } id
            ? await db.RateLists.AsNoTracking().Where(r => r.Id == id).Select(r => r.Id + ":" + r.Revision).FirstOrDefaultAsync(ct) ?? "default"
            : "default";
        var loads = db.LoadPoints.AsNoTracking().Where(l => l.ProjectId == projectId);
        var assumptions = db.Assumptions.AsNoTracking().Where(a => a.ProjectId == projectId);
        return new DocumentSourceSet(project.RulesRef, lv, Latest(DesignKinds.Mv), Latest(DesignKinds.Bulk), Latest(DesignKinds.Options), cp, rate,
            await loads.CountAsync(ct), await loads.MaxAsync(l => (DateTimeOffset?)l.UpdatedAt, ct),
            await assumptions.CountAsync(a => a.Status == AssumptionStatus.Open, ct), await assumptions.MaxAsync(a => (DateTimeOffset?)a.UpdatedAt, ct));
    }
}
