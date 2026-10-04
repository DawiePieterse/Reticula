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

/// <summary>Lifetime-cost overrides; null keeps the rate list's default.</summary>
public sealed record LifetimeParameters(int? PeriodYears, double? DiscountRatePct, double? EnergyCostPerKwh, double? LoadGrowthPct);

/// <summary>What the engineer asks the option search for (plan 5.5).</summary>
public sealed record OptionSearchParameters(Guid TransformerCandidateId, IReadOnlyList<string> Constructions, IReadOnlyList<string> Objectives,
    double? CapexCeiling, LifetimeParameters? Lifetime, bool AllowMove, double? MoveRadiusM, int? MaxEvaluations);

/// <summary>Runs the option search (plan Phase 5) for one transformer site with the same field data as the LV design.</summary>
public sealed class OptionSearchJob(ReticulaDbContext db, ICalcClient calc, DesignInputs inputs, TimeProvider time) : IJobHandler
{
    public const string JobKind = "design.options";
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
            await context.Progress.ReportAsync(20, "Searching designs (every candidate is designed and checked in full)", ct);
            var result = await calc.OptimiseLvAsync(request, ct);
            await context.Progress.ReportAsync(90, "Storing the result", ct);
            var options = result.GetProperty("options").EnumerateArray().ToList();
            var passed = options.Count > 0 && options.All(o => o.GetProperty("option").GetProperty("passed").GetBoolean());
            var summary = new
            {
                passed,
                evaluations = result.GetProperty("evaluations").GetInt32(),
                options = options.Select(o => new
                {
                    objective = o.GetProperty("objective").GetString(),
                    construction = o.GetProperty("design").GetProperty("construction").GetString(),
                    capex = o.GetProperty("option").GetProperty("cost").GetProperty("total").GetDouble(),
                    lifetime = o.GetProperty("lifetime").GetProperty("total").GetProperty("value").GetDouble(),
                    passed = o.GetProperty("option").GetProperty("passed").GetBoolean(),
                }),
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
        var p = JsonSerializer.Deserialize<OptionSearchParameters>(run.ParametersJson, Json) ?? throw new InvalidOperationException("The run has no parameters.");
        var project = await db.Projects.AsNoTracking().FirstAsync(x => x.Id == run.ProjectId, ct);
        var site = await db.Candidates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == p.TransformerCandidateId && c.ProjectId == run.ProjectId
            && c.ArchivedAt == null && (c.Kind == CandidateKinds.Transformer || c.Kind == CandidateKinds.MiniSub), ct)
            ?? throw new InvalidOperationException("The transformer site is not a transformer or mini-sub candidate of this project.");
        var routes = await inputs.RoutesAsync(run.ProjectId, CandidateKinds.LvRoute, ct);
        if (routes.Count == 0) throw new InvalidOperationException("Mark at least one LV route on the field screen before searching options.");
        var customers = await inputs.CustomersAsync(run.ProjectId, ct);
        var fault = await inputs.SourceFaultAsync(run.ProjectId, ct);
        var point = (Point)site.Geometry;
        return new
        {
            Rules = project.RulesRef,
            Source = new[] { point.X, point.Y },
            SourceInspected = true,
            Routes = routes,
            Customers = customers,
            p.Constructions,
            p.Objectives,
            p.CapexCeiling,
            Lifetime = p.Lifetime is null ? null : new { p.Lifetime.PeriodYears, p.Lifetime.DiscountRatePct, p.Lifetime.EnergyCostPerKwh, p.Lifetime.LoadGrowthPct },
            p.AllowMove,
            p.MoveRadiusM,
            p.MaxEvaluations,
            Roads = await inputs.RoadsAsync(run.ProjectId, ct),
            Area = PolygonDto.From(project.Area),
            SourceFaultMvaMax = fault.Max,
            SourceFaultMvaMin = fault.Min,
            Rates = await inputs.RatesAsync(run.ProjectId, ct),
        };
    }
}
