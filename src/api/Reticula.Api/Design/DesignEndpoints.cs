using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Reticula.Api.Jobs;
using Reticula.Domain.Auth;
using Reticula.Domain.Design;
using Reticula.Domain.Field;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Api.Design;

public sealed record LvDesignRequest(Guid TransformerCandidateId, IReadOnlyList<string>? Constructions, double? TransformerKva,
    Dictionary<string, double>? Site);

public sealed record MvDesignRequest(IReadOnlyList<Guid>? SiteIds, string? LvConstruction, string? MvConstruction, double[]? Supply);

public sealed record OptionSearchRequest(Guid TransformerCandidateId, IReadOnlyList<string>? Constructions, IReadOnlyList<string>? Objectives,
    double? CapexCeiling, LifetimeParameters? Lifetime, bool? AllowMove, double? MoveRadiusM, int? MaxEvaluations);

public sealed record DesignRunDto(
    Guid Id, string Kind, string Status, Guid? JobId, JsonElement Parameters, string RulesRef, string? RulesHash, bool? Passed,
    JsonElement? Summary, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

public sealed record DesignRunDetail(DesignRunDto Run, JsonElement? Result);

public sealed record StartedDesign(DesignRunDto Run, JobDto Job);

public static class DesignEndpoints
{
    private static readonly string[] Constructions = ["overhead", "underground"];
    private static readonly string[] Objectives = ["capex", "lifetime", "spare"];

    public static IEndpointRouteBuilder MapDesignEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{projectId:guid}/lv-designs").WithTags("Design").RequireAuthorization(Policies.FieldUser);
        g.MapPost("/", Start).RequireAuthorization(Policies.Engineer);
        g.MapGet("/", (Guid projectId, ReticulaDbContext db, CancellationToken ct) => ListKind(projectId, DesignKinds.Lv, db, ct));
        g.MapGet("/{runId:guid}", (Guid projectId, Guid runId, ReticulaDbContext db, CancellationToken ct) => Get(projectId, runId, DesignKinds.Lv, db, ct));

        var mv = app.MapGroup("/api/projects/{projectId:guid}/mv-designs").WithTags("Design").RequireAuthorization(Policies.FieldUser);
        mv.MapPost("/", StartMv).RequireAuthorization(Policies.Engineer);
        mv.MapGet("/", (Guid projectId, ReticulaDbContext db, CancellationToken ct) => ListKind(projectId, DesignKinds.Mv, db, ct));
        mv.MapGet("/{runId:guid}", (Guid projectId, Guid runId, ReticulaDbContext db, CancellationToken ct) => Get(projectId, runId, DesignKinds.Mv, db, ct));

        var opt = app.MapGroup("/api/projects/{projectId:guid}/option-searches").WithTags("Design").RequireAuthorization(Policies.FieldUser);
        opt.MapPost("/", StartOptions).RequireAuthorization(Policies.Engineer);
        opt.MapGet("/", (Guid projectId, ReticulaDbContext db, CancellationToken ct) => ListKind(projectId, DesignKinds.Options, db, ct));
        opt.MapGet("/{runId:guid}", (Guid projectId, Guid runId, ReticulaDbContext db, CancellationToken ct) => Get(projectId, runId, DesignKinds.Options, db, ct));
        return app;
    }

    internal static async Task<Results<Accepted<StartedDesign>, NotFound, ValidationProblem>> Start(
        Guid projectId, LvDesignRequest req, ReticulaDbContext db, IJobQueue queue, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.ArchivedAt == null, ct);
        if (project is null) return TypedResults.NotFound();

        var errors = new Dictionary<string, string[]>();
        var constructions = (req.Constructions is { Count: > 0 } c ? c : ["overhead"]).Distinct().ToList();
        if (constructions.Any(x => !Constructions.Contains(x))) errors["constructions"] = ["Construction must be overhead and/or underground."];
        var site = await db.Candidates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.TransformerCandidateId && x.ProjectId == projectId && x.ArchivedAt == null, ct);
        if (site is null || site.Kind is not (CandidateKinds.Transformer or CandidateKinds.MiniSub))
            errors["transformerCandidateId"] = ["Choose a transformer or mini-sub site marked on the field screen."];
        if (req.TransformerKva is <= 0) errors["transformerKva"] = ["The transformer rating must be more than 0 kVA."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var parameters = new LvDesignParameters(req.TransformerCandidateId, constructions, req.TransformerKva, req.Site);
        var run = new DesignRun(Guid.CreateVersion7(), projectId, DesignKinds.Lv, JsonSerializer.Serialize(parameters, JsonSerializerOptions.Web),
            project.RulesRef, userId, time.GetUtcNow());
        db.DesignRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var job = await queue.EnqueueAsync(LvDesignJob.JobKind, new { designRunId = run.Id }, userId, projectId, ct);
        run.Queue(job.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted($"/api/projects/{projectId}/lv-designs/{run.Id}", new StartedDesign(ToDto(run), JobDto.From(job)));
    }

    internal static async Task<Results<Accepted<StartedDesign>, NotFound, ValidationProblem>> StartMv(
        Guid projectId, MvDesignRequest req, ReticulaDbContext db, IJobQueue queue, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.ArchivedAt == null, ct);
        if (project is null) return TypedResults.NotFound();
        var errors = new Dictionary<string, string[]>();
        var lv = req.LvConstruction ?? "overhead";
        var mvc = req.MvConstruction ?? "overhead";
        if (!Constructions.Contains(lv)) errors["lvConstruction"] = ["LV construction must be overhead or underground."];
        if (!Constructions.Contains(mvc)) errors["mvConstruction"] = ["MV construction must be overhead or underground."];
        if (req.Supply is not (null or { Length: 2 })) errors["supply"] = ["The supply point is [longitude, latitude]."];
        if (req.SiteIds is { Count: > 0 } ids)
        {
            var known = await db.Candidates.CountAsync(c => ids.Contains(c.Id) && c.ProjectId == projectId && c.ArchivedAt == null
                && (c.Kind == CandidateKinds.Transformer || c.Kind == CandidateKinds.MiniSub), ct);
            if (known != ids.Distinct().Count()) errors["siteIds"] = ["Every site must be a transformer or mini-sub site of this project."];
        }
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var parameters = new MvDesignParameters(req.SiteIds, lv, mvc, req.Supply);
        var run = new DesignRun(Guid.CreateVersion7(), projectId, DesignKinds.Mv, JsonSerializer.Serialize(parameters, JsonSerializerOptions.Web), project.RulesRef, userId, time.GetUtcNow());
        db.DesignRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var job = await queue.EnqueueAsync(MvDesignJob.JobKind, new { designRunId = run.Id }, userId, projectId, ct);
        run.Queue(job.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted($"/api/projects/{projectId}/mv-designs/{run.Id}", new StartedDesign(ToDto(run), JobDto.From(job)));
    }

    internal static async Task<Results<Accepted<StartedDesign>, NotFound, ValidationProblem>> StartOptions(
        Guid projectId, OptionSearchRequest req, ReticulaDbContext db, IJobQueue queue, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.ArchivedAt == null, ct);
        if (project is null) return TypedResults.NotFound();
        var errors = new Dictionary<string, string[]>();
        var constructions = (req.Constructions is { Count: > 0 } c ? c : Constructions).Distinct().ToList();
        if (constructions.Any(x => !Constructions.Contains(x))) errors["constructions"] = ["Construction must be overhead and/or underground."];
        var objectives = (req.Objectives is { Count: > 0 } o ? o : Objectives).Distinct().ToList();
        if (objectives.Any(x => !Objectives.Contains(x))) errors["objectives"] = ["Objectives are capex, lifetime and/or spare."];
        var site = await db.Candidates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.TransformerCandidateId && x.ProjectId == projectId && x.ArchivedAt == null, ct);
        if (site is null || site.Kind is not (CandidateKinds.Transformer or CandidateKinds.MiniSub))
            errors["transformerCandidateId"] = ["Choose a transformer or mini-sub site marked on the field screen."];
        if (req.CapexCeiling is <= 0) errors["capexCeiling"] = ["The capex ceiling must be more than 0."];
        if (req.MoveRadiusM is < 0 or > 1000) errors["moveRadiusM"] = ["The move radius must be between 0 and 1000 m."];
        if (req.MaxEvaluations is < 1 or > 2000) errors["maxEvaluations"] = ["The number of designs to check must be between 1 and 2000."];
        if (req.Lifetime is { } l)
        {
            if (l.PeriodYears is < 1 or > 60) errors["lifetime.periodYears"] = ["The period must be between 1 and 60 years."];
            if (l.DiscountRatePct is < 0 or > 30) errors["lifetime.discountRatePct"] = ["The discount rate must be between 0 and 30 %."];
            if (l.EnergyCostPerKwh is < 0) errors["lifetime.energyCostPerKwh"] = ["The energy cost cannot be negative."];
            if (l.LoadGrowthPct is < 0 or > 20) errors["lifetime.loadGrowthPct"] = ["Demand growth must be between 0 and 20 % a year."];
        }
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var parameters = new OptionSearchParameters(req.TransformerCandidateId, constructions, objectives, req.CapexCeiling, req.Lifetime, req.AllowMove ?? true,
            req.MoveRadiusM, req.MaxEvaluations);
        var run = new DesignRun(Guid.CreateVersion7(), projectId, DesignKinds.Options, JsonSerializer.Serialize(parameters, JsonSerializerOptions.Web), project.RulesRef, userId, time.GetUtcNow());
        db.DesignRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var job = await queue.EnqueueAsync(OptionSearchJob.JobKind, new { designRunId = run.Id }, userId, projectId, ct);
        run.Queue(job.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted($"/api/projects/{projectId}/option-searches/{run.Id}", new StartedDesign(ToDto(run), JobDto.From(job)));
    }

    internal static async Task<Ok<List<DesignRunDto>>> ListKind(Guid projectId, string kind, ReticulaDbContext db, CancellationToken ct)
    {
        var runs = await db.DesignRuns.AsNoTracking().Where(r => r.ProjectId == projectId && r.Kind == kind)
            .OrderByDescending(r => r.CreatedAt).Take(50).ToListAsync(ct);
        return TypedResults.Ok(runs.Select(ToDto).ToList());
    }

    internal static async Task<Results<Ok<DesignRunDetail>, NotFound>> Get(Guid projectId, Guid runId, string kind, ReticulaDbContext db, CancellationToken ct)
    {
        var run = await db.DesignRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId && r.ProjectId == projectId && r.Kind == kind, ct);
        if (run is null) return TypedResults.NotFound();
        return TypedResults.Ok(new DesignRunDetail(ToDto(run), run.ResultJson is null ? null : JsonDocument.Parse(run.ResultJson).RootElement.Clone()));
    }

    internal static DesignRunDto ToDto(DesignRun r) => new(
        r.Id, r.Kind, r.Status.ToString().ToLowerInvariant(), r.JobId, JsonDocument.Parse(r.ParametersJson).RootElement.Clone(), r.RulesRef, r.RulesHash,
        r.Passed, r.SummaryJson is null ? null : JsonDocument.Parse(r.SummaryJson).RootElement.Clone(), r.Error, r.CreatedAt, r.FinishedAt);
}
