using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Reticula.Domain.Design;
using Reticula.Domain.Documents;
using Reticula.Domain.Field;
using Reticula.Domain.Layout;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Infrastructure.Documents;

/// <summary>
/// "Generate all" (plan 6.7): gathers the design runs named in the set's sources, the loads, connection point,
/// assumptions and stands into a package, has the calc service make every document, and stores the files.
/// </summary>
public sealed class DocumentsJob(ReticulaDbContext db, ICalcClient calc, DesignInputs inputs, IFileStore files, TimeProvider time) : IJobHandler
{
    public const string JobKind = "documents.generate";

    public string Kind => JobKind;

    public async Task<object?> RunAsync(JobContext context, CancellationToken ct)
    {
        var setId = context.Payload.GetProperty("documentSetId").GetGuid();
        var set = await db.DocumentSets.FirstAsync(s => s.Id == setId, ct);
        set.Start();
        await db.SaveChangesAsync(ct);
        try
        {
            await context.Progress.ReportAsync(10, "Gathering the design results", ct);
            var package = await PackageAsync(set, ct);
            await context.Progress.ReportAsync(30, "Making drawings, report, BoQ, GIS data and the submission pack", ct);
            var result = await calc.RenderDocumentsAsync(new JsonObject { ["package"] = package }, ct);
            await context.Progress.ReportAsync(85, "Storing the documents", ct);
            var now = time.GetUtcNow();
            foreach (var f in result.GetProperty("files").EnumerateArray())
            {
                var name = f.GetProperty("name").GetString()!;
                var key = $"projects/{set.ProjectId:N}/documents/{set.Id:N}/{name}";
                var bytes = Convert.FromBase64String(f.GetProperty("data_b64").GetString()!);
                await using (var ms = new MemoryStream(bytes))
                    await files.SaveAsync(key, ms, ct);
                db.ProjectDocuments.Add(new ProjectDocument(Guid.CreateVersion7(), set.Id, set.ProjectId, f.GetProperty("kind").GetString()!, name,
                    f.GetProperty("title").GetString()!, f.GetProperty("content_type").GetString()!, key, bytes.LongLength, f.GetProperty("sha256").GetString()!, now));
            }
            set.Succeed(result.GetProperty("checklist").GetRawText(), result.GetProperty("warnings").GetRawText(), now);
            await db.SaveChangesAsync(ct);
            return new { documentSetId = set.Id, files = result.GetProperty("files").GetArrayLength() };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            set.Fail(e is CalcRejectedException or InvalidOperationException ? e.Message : "The documents could not be made; see the job log.", time.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<JsonObject> PackageAsync(DocumentSet set, CancellationToken ct)
    {
        var sources = DocumentSourceSet.FromJson(set.SourcesJson) ?? throw new InvalidOperationException("The document set has no sources.");
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == set.ProjectId, ct);
        var ids = sources.Lv.Values.Select(r => r.RunId).Concat(new[] { sources.Mv, sources.Bulk, sources.Options }.OfType<SourceRun>().Select(r => r.RunId)).ToList();
        var runs = await db.DesignRuns.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        if (sources.Lv.Count == 0) throw new InvalidOperationException("Run an LV design before generating documents.");

        var sites = await db.Candidates.AsNoTracking().Where(c => c.ProjectId == set.ProjectId && (c.Kind == CandidateKinds.Transformer || c.Kind == CandidateKinds.MiniSub))
            .OrderBy(c => c.CreatedAt).ToListAsync(ct);
        var lv = new JsonArray();
        foreach (var (siteId, src) in sources.Lv)
        {
            var run = runs[src.RunId];
            var i = sites.FindIndex(s => s.Id.ToString() == siteId);
            var label = i >= 0 && !string.IsNullOrWhiteSpace(sites[i].Notes) ? sites[i].Notes! : $"Transformer {(i >= 0 ? i + 1 : lv.Count + 1)}";
            lv.Add(new JsonObject { ["site_id"] = siteId, ["label"] = label, ["run_id"] = run.Id.ToString(), ["result"] = JsonNode.Parse(run.ResultJson!) });
        }
        JsonObject? Stored(SourceRun? s) => s is not null && runs.TryGetValue(s.RunId, out var r)
            ? new JsonObject { ["run_id"] = r.Id.ToString(), ["result"] = JsonNode.Parse(r.ResultJson!) }
            : null;

        var loadRows = await (
            from l in db.LoadPoints.AsNoTracking()
            where l.ProjectId == set.ProjectId
            join b in db.Buildings.AsNoTracking() on l.BuildingId equals b.Id
            join s in db.Stands.AsNoTracking() on b.StandId equals s.Id into st
            from s in st.DefaultIfEmpty()
            select new { l, b.Location, Erf = s == null ? null : s.ErfNumber }).ToListAsync(ct);
        var loads = new JsonArray([.. loadRows.Select(x => (JsonNode)new JsonObject
        {
            ["building_id"] = x.l.BuildingId.ToString(), ["erf"] = x.Erf, ["kind"] = x.l.Kind, ["load_class"] = x.l.Kind == LoadKinds.Residential ? x.l.Category : x.l.SpecialLoad,
            ["kva"] = x.l.Kva, ["phases"] = x.l.Phases, ["status"] = x.l.Status.ToString().ToLowerInvariant(), ["overridden"] = x.l.Overridden,
            ["override_reason"] = x.l.OverrideReason, ["lon"] = x.Location.X, ["lat"] = x.Location.Y,
        })]);
        var assumptions = await db.Assumptions.AsNoTracking().Where(a => a.ProjectId == set.ProjectId).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var stands = await db.Stands.AsNoTracking().Where(s => s.ProjectId == set.ProjectId).Select(s => new { s.ErfNumber, s.Geometry }).ToListAsync(ct);
        var cp = await db.ConnectionPoints.AsNoTracking().FirstOrDefaultAsync(c => c.ProjectId == set.ProjectId, ct);
        var rates = await inputs.RatesAsync(set.ProjectId, ct);
        var lvResults = sources.Lv.Values.Select(s => runs[s.RunId]).ToList();
        var designDate = runs.Values.Max(r => r.FinishedAt ?? r.CreatedAt);
        var rateDate = rates is JsonObject ro ? ro["rate_date"]!.GetValue<string>() : null;
        if (rateDate is null)
        {
            // The shipped list: its date as the LV design's cost estimate recorded it.
            using var shipped = JsonDocument.Parse(lvResults[0].ResultJson!);
            rateDate = shipped.RootElement.TryGetProperty("options", out var opts) && opts.GetArrayLength() > 0
                && opts[0].TryGetProperty("cost", out var cost) && cost.TryGetProperty("rate_date", out var d) ? d.GetString() : null;
        }

        return new JsonObject
        {
            ["project"] = new JsonObject { ["id"] = project.Id.ToString(), ["name"] = project.Name, ["authority"] = project.Authority },
            ["stamp"] = new JsonObject
            {
                ["rules"] = project.RulesRef, ["rules_hash"] = lvResults[0].RulesHash ?? "", ["rate_list"] = rates is JsonObject r2 ? r2["name"]!.GetValue<string>() : (string)rates,
                ["rate_date"] = rateDate ?? "", ["design_date"] = designDate.ToString("yyyy-MM-dd"), ["revision"] = set.Revision,
                ["generated_at"] = time.GetUtcNow().ToString("yyyy-MM-dd HH:mm 'UTC'"), ["engineer"] = set.Engineer,
            },
            ["lv_designs"] = lv,
            ["mv_design"] = Stored(sources.Mv),
            ["bulk_study"] = Stored(sources.Bulk),
            ["option_search"] = Stored(sources.Options),
            ["loads"] = loads,
            ["connection_point"] = cp is null ? null : new JsonObject
            {
                ["lon"] = cp.Location.X, ["lat"] = cp.Location.Y, ["voltage_kv"] = cp.VoltageKv, ["available_capacity_kva"] = cp.AvailableCapacityKva,
                ["fault_mva_max"] = cp.FaultMvaMax, ["fault_mva_min"] = cp.FaultMvaMin, ["reference"] = cp.Reference,
            },
            ["assumptions"] = new JsonArray([.. assumptions.Select(a => (JsonNode)new JsonObject
            {
                ["text"] = a.Text, ["source"] = a.SubjectType, ["status"] = a.Status.ToString().ToLowerInvariant(),
            })]),
            ["stands"] = new JsonArray([.. stands.Select(s => (JsonNode)new JsonObject
            {
                ["erf"] = s.ErfNumber,
                ["coordinates"] = new JsonArray([.. new[] { s.Geometry.ExteriorRing }.Concat(s.Geometry.InteriorRings)
                    .Select(ring => (JsonNode)new JsonArray([.. ring.Coordinates.Select(c => (JsonNode)new JsonArray(c.X, c.Y))]))]),
            })]),
            ["rates"] = rates is JsonObject ro2 ? ro2.DeepClone() : JsonValue.Create((string)rates),
        };
    }
}
