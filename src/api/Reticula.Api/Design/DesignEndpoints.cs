using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Reticula.Api.Infrastructure;
using Reticula.Api.Jobs;
using Reticula.Domain.Auth;
using Reticula.Domain.Design;
using Reticula.Domain.Jobs;
using Reticula.Domain.Layout;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;
using Reticula.Infrastructure.Geo;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Api.Design;

/// <param name="Saved">False when this is only what the imported network says, not yet entered and sourced by the engineer.</param>
/// <param name="Complete">The bulk studies can run: capacity and maximum three-phase fault level are given.</param>
public sealed record ConnectionPointDto(bool Saved, PointDto Location, double? VoltageKv, double? CapacityKva, double? Fault3PhKa, double? Fault3PhMinKa,
    double? Fault1PhKa, double? XOverR, string? Source, DateOnly? ReceivedOn, bool Complete, DateTimeOffset? UpdatedAt, uint? Version, string? From);

public sealed record SaveConnectionPointRequest(PointDto Location, double? VoltageKv, double? CapacityKva, double? Fault3PhKa, double? Fault3PhMinKa,
    double? Fault1PhKa, double? XOverR, string? Source, DateOnly? ReceivedOn, uint? Version);

/// <param name="Current">This run is the project's design: the latest run or adopted option.</param>
/// <param name="Stale">Why the project's inputs no longer match this run, or null.</param>
public sealed record DesignRunDto(Guid Id, int Number, string Mode, Guid? ParentRunId, string RulesRef, string RulesHash, string InputsHash, string? ResultHash,
    string? Construction, bool FitToSubmit, int Checks, int Failures, double? Capex, double? Lifetime, JsonElement? Summary, DateTimeOffset CreatedAt,
    bool Current, string? Stale)
{
    public static DesignRunDto From(DesignRun r, Guid? currentId, string? stale) => From(RunRow.Of(r), currentId, stale);

    public static DesignRunDto From(RunRow r, Guid? currentId, string? stale) => new(r.Id, r.Number, r.Mode, r.ParentRunId, r.RulesRef, r.RulesHash,
        r.InputsHash, r.ResultHash, r.Construction, r.FitToSubmit, r.Checks, r.Failures, r.Capex, r.Lifetime,
        r.SummaryJson is null ? null : JsonDocument.Parse(r.SummaryJson).RootElement.Clone(), r.CreatedAt, r.Id == currentId, stale);
}

/// <summary>A design run without its request and result, for lists.</summary>
public sealed record RunRow(Guid Id, int Number, string Mode, Guid? ParentRunId, string RulesRef, string RulesHash, string InputsHash, string InputsPartsJson,
    string? ResultHash, string? Construction, bool FitToSubmit, int Checks, int Failures, double? Capex, double? Lifetime, string? SummaryJson,
    DateTimeOffset CreatedAt, bool HasResult)
{
    public static RunRow Of(DesignRun r) => new(r.Id, r.Number, r.Mode, r.ParentRunId, r.RulesRef, r.RulesHash, r.InputsHash, r.InputsPartsJson,
        r.ResultHash, r.Construction, r.FitToSubmit, r.Checks, r.Failures, r.Capex, r.Lifetime, r.SummaryJson, r.CreatedAt, r.ResultJson != null);
}

/// <param name="Job">The latest design job when it is running, or failed after the latest run.</param>
public sealed record DesignRunsStatus(IReadOnlyList<DesignRunDto> Runs, JobDto? Job);

/// <param name="Options">The engineer's options the run was made with (calc design.DesignOptions, snake_case).</param>
/// <param name="Result">The calc service's result, unchanged: a Design, or an optimisation result (snake_case).</param>
public sealed record DesignRunDetail(DesignRunDto Run, JsonElement Options, JsonElement? Optimise, JsonElement? Result);

/// <param name="Mode">run (default) or optimise.</param>
public sealed record StartDesignRunRequest(string? Mode, JsonElement? Options, JsonElement? Optimise);

public sealed record AdoptRequest(string Objective);

/// <summary>
/// The design run (ADR 0011): the connection point it needs (plan 4.1), runs and optimisations, and adopting an option (plan 5.4).
/// Results are the calc service's JSON, unchanged.
/// </summary>
public static class DesignEndpoints
{
    public static IEndpointRouteBuilder MapDesignEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{projectId:guid}").WithTags("Design");
        g.MapGet("/connection-point", GetConnectionPoint).RequireAuthorization(Policies.FieldUser);
        g.MapPut("/connection-point", SaveConnectionPoint).RequireAuthorization(Policies.Engineer);
        g.MapGet("/design-runs", ListRuns).RequireAuthorization(Policies.FieldUser);
        g.MapPost("/design-runs", StartRun).RequireAuthorization(Policies.Engineer);
        g.MapGet("/design-runs/{runId:guid}", GetRun).RequireAuthorization(Policies.FieldUser);
        g.MapGet("/design-runs/{runId:guid}/request", GetRequest).RequireAuthorization(Policies.Engineer);
        g.MapPost("/design-runs/{runId:guid}/adopt", Adopt).RequireAuthorization(Policies.Engineer);
        g.MapGet("/design", Current).RequireAuthorization(Policies.FieldUser);
        return app;
    }

    // ---------- connection point ----------

    private static async Task<Results<Ok<ConnectionPointDto>, NoContent, NotFound>> GetConnectionPoint(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        if (!await db.Projects.AnyActiveAsync(projectId, ct)) return TypedResults.NotFound();
        if (await db.ConnectionPoints.AsNoTracking().FirstOrDefaultAsync(c => c.ProjectId == projectId, ct) is { } c)
            return TypedResults.Ok(ToDto(c));
        // Before the engineer enters it: what the authority's network file says, to start from.
        var asset = await db.NetworkAssets.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.AssetType == NetworkAssetTypes.ConnectionPoint).OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
        if (asset is null) return TypedResults.NoContent();
        var at = asset.Geometry.Centroid;
        return TypedResults.Ok(new ConnectionPointDto(false, PointDto.Of(at.X, at.Y), asset.VoltageKv, asset.CapacityKva, asset.FaultLevelKa, null, null, null,
            null, null, false, null, null, $"imported network: {asset.Label ?? asset.SourceRef}"));
    }

    private static async Task<Results<Ok<ConnectionPointDto>, ValidationProblem, NotFound, Conflict>> SaveConnectionPoint(Guid projectId,
        SaveConnectionPointRequest req, ReticulaDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await db.Projects.AnyActiveAsync(projectId, ct)) return TypedResults.NotFound();
        var errors = new Dictionary<string, string[]>();
        if (req.Location is null || !req.Location.TryToPoint(out var point, out var error) || point!.X is < -180 or > 180 || point.Y is < -90 or > 90)
        {
            errors["location"] = ["A longitude and latitude are required."];
            point = null;
        }
        void Positive(string name, double? v, double max)
        {
            if (v is { } x && (!double.IsFinite(x) || x <= 0 || x > max)) errors[name] = [$"Must be more than 0 and at most {max}."];
        }
        Positive("voltageKv", req.VoltageKv, 132);
        Positive("capacityKva", req.CapacityKva, 1_000_000);
        Positive("fault3PhKa", req.Fault3PhKa, 100);
        Positive("fault3PhMinKa", req.Fault3PhMinKa, 100);
        Positive("fault1PhKa", req.Fault1PhKa, 100);
        Positive("xOverR", req.XOverR, 100);
        if (req.Fault3PhMinKa > req.Fault3PhKa) errors["fault3PhMinKa"] = ["The minimum fault level cannot exceed the maximum."];
        var source = string.IsNullOrWhiteSpace(req.Source) ? null : req.Source.Trim();
        if (source is { Length: > 500 }) errors["source"] = ["At most 500 characters."];
        if (source is null && (req.CapacityKva is not null || req.Fault3PhKa is not null || req.Fault1PhKa is not null))
            errors["source"] = ["Say where the capacity and fault level come from: the authority's letter or quotation reference."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var now = time.GetUtcNow();
        var c = await db.ConnectionPoints.FirstOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if (c is null)
        {
            c = new ConnectionPoint(projectId, point!, user.UserId(), now);
            db.ConnectionPoints.Add(c);
        }
        else if (req.Version is { } v && v != c.Version) return TypedResults.Conflict();
        c.Update(point!, req.VoltageKv, req.CapacityKva, req.Fault3PhKa, req.Fault3PhMinKa, req.Fault1PhKa, req.XOverR, source, req.ReceivedOn, user.UserId(), now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return TypedResults.Conflict();
        }
        return TypedResults.Ok(ToDto(c));
    }

    private static ConnectionPointDto ToDto(ConnectionPoint c) => new(true, PointDto.From(c.Location), c.VoltageKv, c.CapacityKva, c.Fault3PhKa,
        c.Fault3PhMinKa, c.Fault1PhKa, c.XOverR, c.Source, c.ReceivedOn, c.IsComplete, c.UpdatedAt, c.Version, null);

    // ---------- design runs ----------

    private static async Task<Results<Ok<DesignRunsStatus>, NotFound>> ListRuns(Guid projectId, ReticulaDbContext db, DesignInputsBuilder inputs,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await db.Projects.ActiveAsync(projectId, ct);
        if (project is null) return TypedResults.NotFound();
        var runs = await db.DesignRuns.AsNoTracking().Where(r => r.ProjectId == projectId).OrderByDescending(r => r.Number)
            .Select(r => new RunRow(r.Id, r.Number, r.Mode, r.ParentRunId, r.RulesRef, r.RulesHash, r.InputsHash, r.InputsPartsJson, r.ResultHash,
                r.Construction, r.FitToSubmit, r.Checks, r.Failures, r.Capex, r.Lifetime, r.SummaryJson, r.CreatedAt, r.ResultJson != null))
            .ToListAsync(ct);
        var current = runs.FirstOrDefault(r => r.HasResult && r.Mode is DesignRunModes.Run or DesignRunModes.Adopted)?.Id;
        var now = runs.Count == 0 ? null : await inputs.BuildAsync(project, user.UserId(), ct);
        var job = await LatestJobAsync(db, projectId, DesignRunJob.JobKind, ct);
        if (job is { IsFinished: true } && runs.FirstOrDefault() is { } latest && job.CreatedAt <= latest.CreatedAt) job = null;
        return TypedResults.Ok(new DesignRunsStatus(
            [.. runs.Select(r => DesignRunDto.From(r, current, now is null || now.Hash == r.InputsHash ? null : now.StaleReason(r.InputsPartsJson)))],
            job is null ? null : JobDto.From(job)));
    }

    /// <summary>Starts a design run or an optimisation, or returns the design job already under way.</summary>
    private static async Task<Results<Accepted<JobDto>, ValidationProblem, NotFound>> StartRun(Guid projectId, StartDesignRunRequest req, ReticulaDbContext db,
        IJobQueue queue, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await db.Projects.AnyActiveAsync(projectId, ct)) return TypedResults.NotFound();
        var mode = req.Mode ?? DesignRunModes.Run;
        var errors = RunOptions.ValidateDesign(req.Options);
        if (mode is not (DesignRunModes.Run or DesignRunModes.Optimise)) errors["mode"] = ["Must be run or optimise."];
        if (mode == DesignRunModes.Optimise) foreach (var e in RunOptions.ValidateOptimise(req.Optimise)) errors[e.Key] = e.Value;
        else if (req.Optimise is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) }) errors["optimise"] = ["Only for mode optimise."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var running = await LatestJobAsync(db, projectId, DesignRunJob.JobKind, ct);
        var run = running is { IsFinished: false }
            ? running
            : await queue.EnqueueAsync(DesignRunJob.JobKind, new DesignRunPayload(mode, req.Options, req.Optimise), user.UserId(), projectId, ct);
        return TypedResults.Accepted($"/api/jobs/{run.Id}", JobDto.From(run));
    }

    private static async Task<Results<Ok<DesignRunDetail>, NotFound>> GetRun(Guid projectId, Guid runId, ReticulaDbContext db, DesignInputsBuilder inputs,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await db.Projects.ActiveAsync(projectId, ct);
        var run = project is null ? null : await db.DesignRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId && r.ProjectId == projectId, ct);
        if (run is null) return TypedResults.NotFound();
        return TypedResults.Ok(await DetailAsync(db, inputs, project!, run, user.UserId(), ct));
    }

    private static async Task<Results<Ok<DesignRunDetail>, NoContent, NotFound>> Current(Guid projectId, ReticulaDbContext db, DesignInputsBuilder inputs,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await db.Projects.ActiveAsync(projectId, ct);
        if (project is null) return TypedResults.NotFound();
        var run = await DesignRuns.CurrentAsync(db, projectId, ct);
        if (run is null) return TypedResults.NoContent();
        return TypedResults.Ok(await DetailAsync(db, inputs, project, run, user.UserId(), ct));
    }

    private static async Task<DesignRunDetail> DetailAsync(ReticulaDbContext db, DesignInputsBuilder inputs, Domain.Projects.Project project, DesignRun run,
        Guid userId, CancellationToken ct)
    {
        var current = (await DesignRuns.CurrentAsync(db, project.Id, ct))?.Id;
        var now = await inputs.BuildAsync(project, userId, ct);
        using var request = JsonDocument.Parse(run.RequestJson);
        var root = request.RootElement;
        var design = root.TryGetProperty("design", out var d) ? d : root;
        var options = design.TryGetProperty("options", out var o) ? o.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
        JsonElement? optimise = run.Mode == DesignRunModes.Optimise && root.TryGetProperty("options", out var oo) ? oo.Clone() : null;
        return new DesignRunDetail(DesignRunDto.From(run, current, now.Hash == run.InputsHash ? null : now.StaleReason(run.InputsPartsJson)), options, optimise,
            run.ResultJson is null ? null : JsonDocument.Parse(run.ResultJson).RootElement.Clone());
    }

    /// <summary>The exact request the calc service was sent, to repeat the run anywhere (plan 7.2).</summary>
    private static async Task<Results<FileContentHttpResult, NotFound>> GetRequest(Guid projectId, Guid runId, ReticulaDbContext db, CancellationToken ct)
    {
        if (!await db.Projects.AnyActiveAsync(projectId, ct)) return TypedResults.NotFound();
        var run = await db.DesignRuns.AsNoTracking().Where(r => r.Id == runId && r.ProjectId == projectId).Select(r => new { r.Number, r.RequestJson })
            .FirstOrDefaultAsync(ct);
        if (run is null) return TypedResults.NotFound();
        return TypedResults.File(Encoding.UTF8.GetBytes(run.RequestJson), "application/json", $"design-run-{run.Number}-request.json");
    }

    private static async Task<Results<Created<DesignRunDto>, ValidationProblem, NotFound>> Adopt(Guid projectId, Guid runId, AdoptRequest req,
        ReticulaDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await db.Projects.ActiveAsync(projectId, ct);
        var run = project is null ? null : await db.DesignRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId && r.ProjectId == projectId, ct);
        if (run is null) return TypedResults.NotFound();
        if (run.Mode != DesignRunModes.Optimise || run.ResultJson is null)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["runId"] = ["Only an optimisation run's options can be adopted."] });
        var adopted = await DesignRuns.AdoptAsync(db, project!, run, req.Objective, user.UserId(), time.GetUtcNow(), ct);
        if (adopted is null)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["objective"] = [$"Run {run.Number} has no '{req.Objective}' option."] });
        return TypedResults.Created($"/api/projects/{projectId}/design-runs/{adopted.Id}", DesignRunDto.From(adopted, adopted.Id, null));
    }

    internal static Task<JobRun?> LatestJobAsync(ReticulaDbContext db, Guid projectId, string kind, CancellationToken ct) =>
        db.JobRuns.AsNoTracking().Where(j => j.ProjectId == projectId && j.Kind == kind).OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
}
