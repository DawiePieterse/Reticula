using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Design;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Infrastructure.Design;

/// <summary>The MV design run the study is made on.</summary>
public sealed record BulkStudyParameters(Guid MvDesignRunId);

/// <summary>
/// Runs a bulk supply study (plan Phase 4): the MV design's network and transformer sites, fed from the authority's
/// connection point, go to the calc service for load flow, fault levels, supply capacity and the NMD.
/// </summary>
public sealed class BulkStudyJob(ReticulaDbContext db, ICalcClient calc, TimeProvider time) : IJobHandler
{
    public const string JobKind = "design.bulk";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
            await context.Progress.ReportAsync(10, "Reading the MV design and the connection point", ct);
            var request = await BuildRequestAsync(run, ct);
            input = request.ToJsonString();
            await context.Progress.ReportAsync(30, "Running the load flow and fault studies", ct);
            var result = await calc.StudyBulkAsync(request, ct);
            await context.Progress.ReportAsync(90, "Storing the result", ct);
            var passed = result.GetProperty("passed").GetBoolean();
            var summary = new
            {
                passed,
                supply_kva = result.GetProperty("supply_kva").GetDouble(),
                notified_max_demand_kva = result.GetProperty("notified_max_demand_kva").GetDouble(),
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

    private async Task<JsonObject> BuildRequestAsync(DesignRun run, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<BulkStudyParameters>(run.ParametersJson, Json) ?? throw new InvalidOperationException("The study has no parameters.");
        var cp = await db.ConnectionPoints.AsNoTracking().FirstOrDefaultAsync(c => c.ProjectId == run.ProjectId, ct)
                 ?? throw new InvalidOperationException("Enter the authority's connection point first.");
        if (cp.MissingForStudy() is { Count: > 0 } missing)
            throw new InvalidOperationException($"The connection point has no {string.Join(" or ", missing)} from the authority.");
        var mv = await db.DesignRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == p.MvDesignRunId && r.ProjectId == run.ProjectId
            && r.Kind == DesignKinds.Mv && r.Status == DesignRunStatus.Succeeded, ct) ?? throw new InvalidOperationException("The MV design run is not a finished MV design of this project.");
        var result = JsonNode.Parse(mv.ResultJson!)!.AsObject();
        if (result["mv_network"] is not JsonObject network) throw new InvalidOperationException("The MV design has no MV network to study.");
        var sites = new JsonArray();
        foreach (var s in result["sites"]!.AsArray())
        {
            var pl = s!["placement"]!;
            if (pl["rating_kva"] is null) continue;
            sites.Add(new JsonObject
            {
                ["site_id"] = pl["site_id"]!.GetValue<string>(),
                ["rating_kva"] = pl["rating_kva"]!.GetValue<double>(),
                ["z_pct"] = pl["z_pct"]?.GetValue<double>() ?? 4.0,
                ["x_r"] = pl["x_r"]?.GetValue<double>() ?? 2.0,
                ["tap_pct"] = s["tap_pct"]?.GetValue<double>(),
                ["design_kva"] = pl["design_kva"]!.GetValue<double>(),
            });
        }
        if (sites.Count == 0) throw new InvalidOperationException("No transformer in the MV design has a rating; fix the MV design first.");
        var point = new JsonObject
        {
            ["lon"] = cp.Location.X,
            ["lat"] = cp.Location.Y,
            ["voltage_kv"] = cp.VoltageKv,
            ["available_capacity_kva"] = cp.AvailableCapacityKva,
            ["fault_mva_max"] = cp.FaultMvaMax,
            ["fault_mva_min"] = cp.FaultMvaMin,
            ["x_r"] = cp.XR,
            ["sending_voltage_pct"] = cp.SendingVoltagePct,
            ["reference"] = cp.Reference,
        };
        return new JsonObject
        {
            ["rules"] = mv.RulesRef,
            ["connection_point"] = point,
            ["mv_network"] = network.DeepClone(),
            ["sites"] = sites,
        };
    }
}
