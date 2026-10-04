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

public sealed record DesignRunDto(
    Guid Id, string Kind, string Status, Guid? JobId, JsonElement Parameters, string RulesRef, string? RulesHash, bool? Passed,
    JsonElement? Summary, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

public sealed record DesignRunDetail(DesignRunDto Run, JsonElement? Result);

public sealed record StartedDesign(DesignRunDto Run, JobDto Job);

public static class DesignEndpoints
{
    private static readonly string[] Constructions = ["overhead", "underground"];

    public static IEndpointRouteBuilder MapDesignEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{projectId:guid}/lv-designs").WithTags("Design").RequireAuthorization(Policies.FieldUser);
        g.MapPost("/", Start).RequireAuthorization(Policies.Engineer);
        g.MapGet("/", List);
        g.MapGet("/{runId:guid}", Get);
        return app;
    }

    private static async Task<Results<Accepted<StartedDesign>, NotFound, ValidationProblem>> Start(
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

    private static async Task<Ok<List<DesignRunDto>>> List(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        var runs = await db.DesignRuns.AsNoTracking().Where(r => r.ProjectId == projectId && r.Kind == DesignKinds.Lv)
            .OrderByDescending(r => r.CreatedAt).Take(50).ToListAsync(ct);
        return TypedResults.Ok(runs.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<DesignRunDetail>, NotFound>> Get(Guid projectId, Guid runId, ReticulaDbContext db, CancellationToken ct)
    {
        var run = await db.DesignRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId && r.ProjectId == projectId, ct);
        if (run is null) return TypedResults.NotFound();
        return TypedResults.Ok(new DesignRunDetail(ToDto(run), run.ResultJson is null ? null : JsonDocument.Parse(run.ResultJson).RootElement.Clone()));
    }

    private static DesignRunDto ToDto(DesignRun r) => new(
        r.Id, r.Kind, r.Status.ToString().ToLowerInvariant(), r.JobId, JsonDocument.Parse(r.ParametersJson).RootElement.Clone(), r.RulesRef, r.RulesHash,
        r.Passed, r.SummaryJson is null ? null : JsonDocument.Parse(r.SummaryJson).RootElement.Clone(), r.Error, r.CreatedAt, r.FinishedAt);
}
