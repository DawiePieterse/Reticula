using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Reticula.Api.Infrastructure;
using Reticula.Api.Jobs;
using Reticula.Domain.Auth;
using Reticula.Domain.Design;
using Reticula.Domain.Jobs;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Api.Design;

public sealed record PlacementDto(Guid Id, string RulesRef, string RulesHash, string Clause, DateTimeOffset BuiltAt, int Transformers, int Loads, int Unplaced,
    CalcPlacement Result)
{
    public static PlacementDto From(Placement p) => new(p.Id, p.RulesRef, p.RulesHash, p.Clause, p.BuiltAt, p.Transformers, p.Loads, p.Unplaced,
        JsonSerializer.Deserialize<CalcPlacement>(p.ResultJson, PlacementJob.Json)!);
}

/// <param name="Placement">The current proposal, if one was made.</param>
/// <param name="Job">The latest run started after it: running, or failed with the reason.</param>
public sealed record PlacementStatus(PlacementDto? Placement, JobDto? Job);

/// <summary>Pre-design placement (ADR 0010): the design proposes transformer sites and routes, the field verifies them.</summary>
public static class PlacementEndpoints
{
    public static IEndpointRouteBuilder MapPlacementEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{projectId:guid}/placement").WithTags("Design");
        g.MapGet("/", Status).RequireAuthorization(Policies.FieldUser);
        g.MapPost("/", Run).RequireAuthorization(Policies.Engineer);
        return app;
    }

    private static async Task<Results<Ok<PlacementStatus>, NotFound>> Status(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        if (!await db.Projects.AnyActiveAsync(projectId, ct)) return TypedResults.NotFound();
        var placement = await db.Placements.AsNoTracking().FirstOrDefaultAsync(p => p.ProjectId == projectId, ct);
        var job = await LatestJobAsync(db, projectId, ct);
        if (job is not null && placement is not null && job.CreatedAt <= placement.BuiltAt && job.IsFinished) job = null;
        return TypedResults.Ok(new PlacementStatus(placement is null ? null : PlacementDto.From(placement), job is null ? null : JobDto.From(job)));
    }

    /// <summary>Starts a placement run, or returns the run already under way.</summary>
    private static async Task<Results<Accepted<JobDto>, NotFound>> Run(Guid projectId, ReticulaDbContext db, IJobQueue queue, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await db.Projects.AnyActiveAsync(projectId, ct)) return TypedResults.NotFound();
        var running = await LatestJobAsync(db, projectId, ct);
        var run = running is { IsFinished: false }
            ? running
            : await queue.EnqueueAsync(PlacementJob.JobKind, null, user.UserId(), projectId, ct);
        return TypedResults.Accepted($"/api/jobs/{run.Id}", JobDto.From(run));
    }

    private static Task<JobRun?> LatestJobAsync(ReticulaDbContext db, Guid projectId, CancellationToken ct) =>
        db.JobRuns.AsNoTracking().Where(j => j.ProjectId == projectId && j.Kind == PlacementJob.JobKind)
            .OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
}
