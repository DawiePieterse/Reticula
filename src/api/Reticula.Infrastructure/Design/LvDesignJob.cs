using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Reticula.Domain.Design;
using Reticula.Domain.Field;
using Reticula.Domain.Layout;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Geo;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Infrastructure.Design;

/// <summary>What the engineer asks for: which transformer site, which constructions, and optionally a fixed transformer size.</summary>
public sealed record LvDesignParameters(Guid TransformerCandidateId, IReadOnlyList<string> Constructions, double? TransformerKva,
    Dictionary<string, double>? Site);

/// <summary>
/// Runs an LV design in the background (plan 2.x): gathers the transformer site, the LV routes, every building present
/// with its load and connection phases and the imported roads, sends them to the calc service, and stores the result
/// on the design run.
/// </summary>
public sealed class LvDesignJob(ReticulaDbContext db, ICalcClient calc, TimeProvider time) : IJobHandler
{
    public const string JobKind = "design.lv";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Snake = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string Kind => JobKind;

    public async Task<object?> RunAsync(JobContext context, CancellationToken ct)
    {
        var runId = context.Payload.GetProperty("designRunId").GetGuid();
        var run = await db.DesignRuns.FirstAsync(r => r.Id == runId, ct);
        run.Start();
        await db.SaveChangesAsync(ct);
        string? input = null;
        try
        {
            await context.Progress.ReportAsync(10, "Gathering field data", ct);
            var request = await BuildRequestAsync(run, ct);
            input = JsonSerializer.Serialize(request, Snake);

            await context.Progress.ReportAsync(30, "Designing (layout, sizing, checks)", ct);
            var result = await calc.DesignLvAsync(request, ct);

            await context.Progress.ReportAsync(90, "Storing the result", ct);
            var comparison = result.GetProperty("comparison");
            var passed = comparison.GetArrayLength() > 0 && comparison[0].GetProperty("passed").GetBoolean();
            run.Succeed(input, result.GetRawText(), result.GetProperty("rules_hash").GetString() ?? "", passed, comparison.GetRawText(), time.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return new { designRunId = run.Id, passed };
        }
        catch (Exception e) when (e is CalcRejectedException or InvalidOperationException)
        {
            run.Fail(e.Message, input, time.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<object> BuildRequestAsync(DesignRun run, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<LvDesignParameters>(run.ParametersJson, Json)
                ?? throw new InvalidOperationException("The design run has no parameters.");
        var project = await db.Projects.AsNoTracking().FirstAsync(x => x.Id == run.ProjectId, ct);
        var site = await db.Candidates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == p.TransformerCandidateId && c.ProjectId == run.ProjectId
            && c.ArchivedAt == null && (c.Kind == CandidateKinds.Transformer || c.Kind == CandidateKinds.MiniSub), ct)
            ?? throw new InvalidOperationException("The transformer site is not a transformer or mini-sub candidate of this project.");
        var routes = await db.Candidates.AsNoTracking()
            .Where(c => c.ProjectId == run.ProjectId && c.ArchivedAt == null && c.Kind == CandidateKinds.LvRoute).ToListAsync(ct);
        if (routes.Count == 0) throw new InvalidOperationException("Mark at least one LV route on the field screen before designing.");

        var rows = await (
            from b in db.Buildings.AsNoTracking()
            where b.ProjectId == run.ProjectId && b.Status != BuildingStatus.NotPresent
            join l in db.LoadPoints.AsNoTracking() on b.Id equals l.BuildingId into lps
            from l in lps.DefaultIfEmpty()
            join s in db.Stands.AsNoTracking() on b.StandId equals s.Id into stands
            from s in stands.DefaultIfEmpty()
            select new { b, l, Erf = s == null ? null : s.ErfNumber }).ToListAsync(ct);
        var missing = rows.Where(x => x.l is null).Select(x => x.Erf ?? x.b.Id.ToString()).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"{missing.Count} buildings have no load recorded (e.g. {string.Join(", ", missing.Take(5))}); record them first.");

        var roads = await db.MapFeatures.AsNoTracking().Where(m => m.ProjectId == run.ProjectId && m.Layer == MapLayers.Roads).Select(m => m.Geometry).ToListAsync(ct);
        var sitePoint = (Point)site.Geometry;
        return new
        {
            Rules = project.RulesRef,
            Source = new[] { sitePoint.X, sitePoint.Y },
            SourceInspected = true,
            Routes = routes.Select(r => new { Id = r.Id.ToString(), Coordinates = ((LineString)r.Geometry).Coordinates.Select(c => new[] { c.X, c.Y }) }),
            Customers = rows.Select(x => new
            {
                BuildingId = x.b.Id.ToString(),
                Lon = x.b.Location.X,
                Lat = x.b.Location.Y,
                Phases = x.l!.Kind == LoadKinds.Residential ? x.l.Phases : 1,
                Kind = x.l.Kind,
                LoadClass = x.l.Kind == LoadKinds.Residential ? x.l.Category : null,
                SpecialKva = x.l.Kind == LoadKinds.Special ? x.l.Kva : (double?)null,
                Inspected = x.b.Status is BuildingStatus.Confirmed or BuildingStatus.New,
                Erf = x.Erf,
            }),
            Constructions = p.Constructions,
            Roads = roads.OfType<LineString>().Select(l => l.Coordinates.Select(c => new[] { c.X, c.Y })),
            TransformerKva = p.TransformerKva,
            p.Site,
            Area = PolygonDto.From(project.Area),
        };
    }
}
