using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Reticula.Domain.Design;
using Reticula.Domain.Documents;
using Reticula.Domain.Field;
using Reticula.Domain.Review;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;
using Reticula.Infrastructure.Documents;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Infrastructure.Review;

/// <summary>What stands between the project and sign-off (plans 7.1, 7.3).</summary>
public sealed record Readiness(IReadOnlyList<string> Blockers, int OpenAssumptions, Guid? DocumentSetId, string? DocumentRevision, bool DocumentsCurrent, string SourcesHash)
{
    public bool CanSignOff => Blockers.Count == 0;
}

public sealed class ReviewReadiness(ReticulaDbContext db, AssumptionRegister register)
{
    public async Task<Readiness> CheckAsync(Guid projectId, CancellationToken ct)
    {
        await register.SyncAsync(projectId, ct);
        var sources = await DocumentSourceSet.CurrentAsync(db, projectId, ct);
        var blockers = new List<string>();
        if (sources.Lv.Count == 0) blockers.Add("Run the LV design.");
        var ids = sources.Lv.Values.Concat(new[] { sources.Mv, sources.Bulk }.OfType<SourceRun>()).Select(r => r.RunId).ToList();
        var runs = await db.DesignRuns.AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        foreach (var r in runs.Where(r => r.Passed == false))
        {
            var what = r.Kind switch { DesignKinds.Lv => "An LV design", DesignKinds.Mv => "The MV design", _ => "The bulk supply study" };
            blockers.Add($"{what} fails checks (run {r.CreatedAt:yyyy-MM-dd HH:mm}); fix the design and run it again.");
        }
        var open = await db.Assumptions.CountAsync(a => a.ProjectId == projectId && a.Status == AssumptionStatus.Open, ct);
        if (open > 0) blockers.Add($"{open} assumption(s) are open; clear or accept each one.");
        var latest = await db.DocumentSets.AsNoTracking().Where(s => s.ProjectId == projectId && s.Status == DocumentSetStatus.Succeeded)
            .OrderByDescending(s => s.Number).FirstOrDefaultAsync(ct);
        var hash = sources.Hash();
        var current = latest is not null && latest.SourcesHash == hash;
        if (latest is null) blockers.Add("Generate the documents and review them.");
        else if (!current) blockers.Add("The documents are out of date; generate them again and review them.");
        return new Readiness(blockers, open, latest?.Id, latest?.Revision, current, hash);
    }
}

/// <summary>
/// Issues a signed-off revision (plans 7.2, 7.3): snapshots the field data and inputs, and makes the lettered,
/// locked document set stamped with the sign-off.
/// </summary>
public sealed class RevisionIssueJob(ReticulaDbContext db, DocumentGenerator generator, DesignInputs inputs, IFileStore files, TimeProvider time) : IJobHandler
{
    public const string JobKind = "revision.issue";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Kind => JobKind;

    public async Task<object?> RunAsync(JobContext context, CancellationToken ct)
    {
        var revision = await db.Revisions.FirstAsync(r => r.Id == context.Payload.GetProperty("revisionId").GetGuid(), ct);
        var set = await db.DocumentSets.FirstAsync(s => s.Id == revision.DocumentSetId, ct);
        try
        {
            await context.Progress.ReportAsync(5, "Taking the snapshot", ct);
            var snapshot = await SnapshotAsync(revision, set, ct);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
            var key = $"projects/{revision.ProjectId:N}/revisions/{revision.Label.ToLowerInvariant()}/snapshot.json";
            await using (var ms = new MemoryStream(bytes))
                await files.SaveAsync(key, ms, ct);
            await generator.GenerateAsync(set, context.Progress, ct);
            revision.Issued(key, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            await db.SaveChangesAsync(ct);
            return new { revisionId = revision.Id, label = revision.Label };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var message = e is CalcRejectedException or InvalidOperationException ? e.Message : "The revision could not be issued; see the job log.";
            revision.Fail(message);
            if (set.Status != DocumentSetStatus.Succeeded) set.Fail(message, time.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<JsonObject> SnapshotAsync(Revision revision, DocumentSet set, CancellationToken ct)
    {
        var pid = revision.ProjectId;
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == pid, ct);
        var cp = await db.ConnectionPoints.AsNoTracking().FirstOrDefaultAsync(c => c.ProjectId == pid, ct);
        var loads = await db.LoadPoints.AsNoTracking().Where(l => l.ProjectId == pid).ToListAsync(ct);
        var buildings = await db.Buildings.AsNoTracking().Where(b => b.ProjectId == pid).Select(b => new { b.Id, b.Status, b.ConfirmedType, b.PredictedType, b.Location, b.StandId }).ToListAsync(ct);
        var candidates = await db.Candidates.AsNoTracking().Where(c => c.ProjectId == pid && c.ArchivedAt == null).ToListAsync(ct);
        var assumptions = await db.Assumptions.AsNoTracking().Where(a => a.ProjectId == pid).ToListAsync(ct);
        return new JsonObject
        {
            ["revision"] = revision.Label,
            ["signedOff"] = new JsonObject { ["name"] = revision.SignedOffName, ["registration"] = revision.RegistrationNumber, ["at"] = revision.SignedOffAt.ToString("O") },
            ["project"] = new JsonObject { ["id"] = project.Id.ToString(), ["name"] = project.Name, ["rules"] = project.RulesRef, ["area"] = project.Area.AsText() },
            ["sources"] = JsonNode.Parse(set.SourcesJson),
            ["rates"] = await inputs.RatesAsync(pid, ct) is JsonObject rates ? rates : JsonValue.Create(DesignInputs.DefaultRates),
            ["connectionPoint"] = cp is null ? null : JsonSerializer.SerializeToNode(new
            {
                Location = cp.Location.AsText(), cp.VoltageKv, cp.AvailableCapacityKva, cp.FaultMvaMax, cp.FaultMvaMin, cp.XR, cp.SendingVoltagePct, cp.Reference, cp.UpdatedAt,
            }, Json),
            ["loads"] = JsonSerializer.SerializeToNode(loads.Select(l => new
            {
                l.Id, l.BuildingId, l.Kind, l.SpecialLoad, Observations = JsonNode.Parse(l.ObservationsJson), l.IncomeBand, l.ClassOverride, l.Category,
                l.EstimatedKva, l.Kva, l.Overridden, l.OverrideReason, l.Phases, Status = l.Status.ToString(), l.RulesHash, l.UpdatedAt,
            }), Json),
            ["buildings"] = JsonSerializer.SerializeToNode(buildings.Select(b => new { b.Id, Status = b.Status.ToString(), b.ConfirmedType, b.PredictedType, Location = b.Location.AsText(), b.StandId }), Json),
            ["candidates"] = JsonSerializer.SerializeToNode(candidates.Select(c => new { c.Id, c.Kind, Geometry = c.Geometry.AsText(), c.Notes }), Json),
            ["assumptions"] = JsonSerializer.SerializeToNode(assumptions.Select(a => new { a.Id, a.SubjectType, a.SubjectId, a.Code, a.Text, Status = a.Status.ToString(), a.ClearedBy, a.ClearedAt, a.ClearNote }), Json),
        };
    }
}

/// <summary>One design run re-run from its stored input and compared with its stored result.</summary>
public sealed record ReproducedRun(string Kind, Guid RunId, bool Identical, string? Difference);

/// <summary>
/// Reproduces a revision (plan 7.2, release gate): every design run it rests on is sent to the calc service again with
/// its stored input, and the new result is compared with the stored one value by value.
/// </summary>
public sealed class RevisionReproduceJob(ReticulaDbContext db, ICalcClient calc, TimeProvider time) : IJobHandler
{
    public const string JobKind = "revision.reproduce";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Kind => JobKind;

    public async Task<object?> RunAsync(JobContext context, CancellationToken ct)
    {
        var revision = await db.Revisions.FirstAsync(r => r.Id == context.Payload.GetProperty("revisionId").GetGuid(), ct);
        var set = await db.DocumentSets.AsNoTracking().FirstAsync(s => s.Id == revision.DocumentSetId, ct);
        var sources = DocumentSourceSet.FromJson(set.SourcesJson) ?? throw new InvalidOperationException("The revision has no sources.");
        var ids = sources.Lv.Values.Concat(new[] { sources.Mv, sources.Bulk, sources.Options }.OfType<SourceRun>()).Select(r => r.RunId).ToList();
        var runs = await db.DesignRuns.AsNoTracking().Where(r => ids.Contains(r.Id)).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        var results = new List<ReproducedRun>();
        var i = 0;
        foreach (var run in runs)
        {
            await context.Progress.ReportAsync(5 + 90 * i++ / Math.Max(runs.Count, 1), $"Re-running the {run.Kind} design", ct);
            if (run.InputJson is null || run.ResultJson is null)
            {
                results.Add(new ReproducedRun(run.Kind, run.Id, false, "no stored input or result"));
                continue;
            }
            var input = JsonNode.Parse(run.InputJson)!;
            var fresh = run.Kind switch
            {
                DesignKinds.Lv => await calc.DesignLvAsync(input, ct),
                DesignKinds.Mv => await calc.DesignMvAsync(input, ct),
                DesignKinds.Bulk => await calc.StudyBulkAsync(input, ct),
                DesignKinds.Options => await calc.OptimiseLvAsync(input, ct),
                _ => throw new InvalidOperationException($"Unknown design kind {run.Kind}."),
            };
            using var stored = JsonDocument.Parse(run.ResultJson);
            var diff = Difference(stored.RootElement, fresh, "$");
            results.Add(new ReproducedRun(run.Kind, run.Id, diff is null, diff));
        }
        var identical = results.Count > 0 && results.All(r => r.Identical);
        revision.RecordReproduction(JsonSerializer.Serialize(results, Json), identical, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return new { revisionId = revision.Id, identical };
    }

    /// <summary>The first place two results differ, or null when every value is the same.</summary>
    public static string? Difference(JsonElement a, JsonElement b, string path)
    {
        if (a.ValueKind != b.ValueKind) return $"{path}: {Show(a)} → {Show(b)}";
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var keys = a.EnumerateObject().Select(p => p.Name).Union(b.EnumerateObject().Select(p => p.Name)).Order(StringComparer.Ordinal);
                foreach (var k in keys)
                {
                    var hasA = a.TryGetProperty(k, out var va);
                    var hasB = b.TryGetProperty(k, out var vb);
                    if (!hasA || !hasB) return $"{path}.{k}: {(hasA ? Show(va) : "missing")} → {(hasB ? Show(vb) : "missing")}";
                    if (Difference(va, vb, $"{path}.{k}") is { } d) return d;
                }
                return null;
            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength()) return $"{path}: {a.GetArrayLength()} items → {b.GetArrayLength()} items";
                for (var i = 0; i < a.GetArrayLength(); i++)
                    if (Difference(a[i], b[i], $"{path}[{i}]") is { } d) return d;
                return null;
            case JsonValueKind.Number:
                return a.GetDouble() == b.GetDouble() ? null : $"{path}: {a.GetRawText()} → {b.GetRawText()}";
            case JsonValueKind.String:
                return a.GetString() == b.GetString() ? null : $"{path}: {Show(a)} → {Show(b)}";
            default:
                return null;
        }
    }

    private static string Show(JsonElement e) => e.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? e.ValueKind.ToString().ToLowerInvariant() : Truncate(e.GetRawText());

    private static string Truncate(string s) => s.Length <= 80 ? s : s[..80] + "…";
}
