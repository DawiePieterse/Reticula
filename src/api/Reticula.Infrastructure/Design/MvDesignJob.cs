using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Reticula.Domain.Design;
using Reticula.Domain.Field;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Geo;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Infrastructure.Design;

/// <summary>Which transformer sites (all marked sites when empty), the LV and MV construction, and optionally the supply point.</summary>
public sealed record MvDesignParameters(IReadOnlyList<Guid>? SiteIds, string LvConstruction, string MvConstruction, double[]? Supply);

/// <summary>Runs an MV design (plan Phase 3): sites, LV and MV routes, loads and the supply point go to the calc service.</summary>
public sealed class MvDesignJob(ReticulaDbContext db, ICalcClient calc, DesignInputs inputs, TimeProvider time) : IJobHandler
{
    public const string JobKind = "design.mv";
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
            await context.Progress.ReportAsync(25, "Placing transformers and designing each LV network and the MV network", ct);
            var result = await calc.DesignMvAsync(request, ct);
            await context.Progress.ReportAsync(90, "Storing the result", ct);
            var passed = result.GetProperty("passed").GetBoolean();
            var summary = new
            {
                passed,
                sites = result.GetProperty("sites").GetArrayLength(),
                cost_total = result.GetProperty("cost_total").GetDouble(),
                failed = result.GetProperty("checks").EnumerateArray().Count(c => !c.GetProperty("passed").GetBoolean()),
            };
            run.Succeed(input, result.GetRawText(), result.GetProperty("rules_hash").GetString() ?? "", passed, JsonSerializer.Serialize(summary), time.GetUtcNow());
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
        var p = JsonSerializer.Deserialize<MvDesignParameters>(run.ParametersJson, Json) ?? throw new InvalidOperationException("The design run has no parameters.");
        var project = await db.Projects.AsNoTracking().FirstAsync(x => x.Id == run.ProjectId, ct);
        var siteRows = await db.Candidates.AsNoTracking().Where(c => c.ProjectId == run.ProjectId && c.ArchivedAt == null
            && (c.Kind == CandidateKinds.Transformer || c.Kind == CandidateKinds.MiniSub)).ToListAsync(ct);
        if (p.SiteIds is { Count: > 0 } ids) siteRows = [.. siteRows.Where(s => ids.Contains(s.Id))];
        if (siteRows.Count == 0) throw new InvalidOperationException("Mark at least one transformer or mini-sub site on the field screen.");
        var lv = await inputs.RoutesAsync(run.ProjectId, CandidateKinds.LvRoute, ct);
        if (lv.Count == 0) throw new InvalidOperationException("Mark the LV routes on the field screen before designing.");
        var mvRows = await db.Candidates.AsNoTracking().Where(c => c.ProjectId == run.ProjectId && c.ArchivedAt == null && c.Kind == CandidateKinds.MvRoute).ToListAsync(ct);
        if (mvRows.Count == 0) throw new InvalidOperationException("Mark the MV route on the field screen before designing the MV network.");
        var mv = await inputs.RoutesAsync(run.ProjectId, CandidateKinds.MvRoute, ct);
        var customers = await inputs.CustomersAsync(run.ProjectId, ct);
        var (supply, note) = await inputs.SupplyAsync(run.ProjectId, p.Supply, [.. siteRows.Select(s => (Point)s.Geometry)], project.Area,
            [.. mvRows.Select(r => (LineString)r.Geometry)], ct);
        var fault = await inputs.SourceFaultAsync(run.ProjectId, ct);
        return new
        {
            Rules = project.RulesRef,
            Supply = supply,
            SupplyNote = note,
            Sites = siteRows.Select(s => new { Id = s.Id.ToString(), Kind = s.Kind, Lon = ((Point)s.Geometry).X, Lat = ((Point)s.Geometry).Y }),
            LvRoutes = lv,
            MvRoutes = mv,
            Customers = customers,
            p.LvConstruction,
            p.MvConstruction,
            Roads = await inputs.RoadsAsync(run.ProjectId, ct),
            Area = PolygonDto.From(project.Area),
            SourceFaultMvaMax = fault.Max,
            SourceFaultMvaMin = fault.Min,
            Rates = await inputs.RatesAsync(run.ProjectId, ct),
        };
    }
}
