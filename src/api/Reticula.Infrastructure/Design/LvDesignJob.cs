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
public sealed class LvDesignJob(ReticulaDbContext db, ICalcClient calc, DesignInputs inputs, TimeProvider time) : IJobHandler
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
        var routes = await inputs.RoutesAsync(run.ProjectId, CandidateKinds.LvRoute, ct);
        if (routes.Count == 0) throw new InvalidOperationException("Mark at least one LV route on the field screen before designing.");
        var customers = await inputs.CustomersAsync(run.ProjectId, ct);
        var roads = await inputs.RoadsAsync(run.ProjectId, ct);
        var sitePoint = (Point)site.Geometry;
        var fault = await inputs.SourceFaultAsync(run.ProjectId, ct);
        return new
        {
            Rules = project.RulesRef,
            Source = new[] { sitePoint.X, sitePoint.Y },
            SourceInspected = true,
            Routes = routes,
            Customers = customers,
            Constructions = p.Constructions,
            Roads = roads,
            TransformerKva = p.TransformerKva,
            p.Site,
            Area = PolygonDto.From(project.Area),
            SourceFaultMvaMax = fault.Max,
            SourceFaultMvaMin = fault.Min,
            Rates = await inputs.RatesAsync(run.ProjectId, ct),
        };
    }
}
