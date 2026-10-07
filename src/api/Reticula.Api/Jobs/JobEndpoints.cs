using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Reticula.Api.Infrastructure;
using Reticula.Domain.Auth;
using Reticula.Domain.Jobs;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Jobs.Handlers;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Api.Jobs;

public sealed record JobDto(
    Guid Id, string Kind, string Status, int ProgressPct, string? Message, string? Error,
    Guid? ProjectId, JsonElement? Result, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt)
{
    public static JobDto From(JobRun r) => new(
        r.Id, r.Kind, r.Status.ToString().ToLowerInvariant(), r.ProgressPct, r.Message, r.Error, r.ProjectId,
        r.ResultJson is null ? null : JsonDocument.Parse(r.ResultJson).RootElement.Clone(),
        r.CreatedAt, r.StartedAt, r.FinishedAt);
}

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/jobs").WithTags("Jobs").RequireAuthorization(Policies.FieldUser);
        g.MapGet("/", List);
        g.MapGet("/{id:guid}", Get);
        g.MapPost("/{id:guid}/cancel", Cancel).RequireAuthorization(Policies.Engineer);

        app.MapPost("/api/system/diagnostics", StartDiagnostics).WithTags("System").RequireAuthorization(Policies.Engineer);
        app.MapHub<JobsHub>(JobsHub.Path);
        return app;
    }

    private static async Task<Ok<List<JobDto>>> List(ReticulaDbContext db, Guid? projectId, int? limit, CancellationToken ct)
    {
        var q = db.JobRuns.AsNoTracking();
        if (projectId is not null) q = q.Where(j => j.ProjectId == projectId);
        var runs = await q.OrderByDescending(j => j.CreatedAt).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);
        return TypedResults.Ok(runs.Select(JobDto.From).ToList());
    }

    private static async Task<Results<Ok<JobDto>, NotFound>> Get(Guid id, ReticulaDbContext db, CancellationToken ct)
    {
        var run = await db.JobRuns.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        return run is null ? TypedResults.NotFound() : TypedResults.Ok(JobDto.From(run));
    }

    private static async Task<Results<Accepted<JobDto>, NotFound>> Cancel(Guid id, IJobQueue queue, ReticulaDbContext db, CancellationToken ct)
    {
        if (!await queue.CancelAsync(id, ct)) return TypedResults.NotFound();
        var run = await db.JobRuns.AsNoTracking().FirstAsync(j => j.Id == id, ct);
        return TypedResults.Accepted($"/api/jobs/{id}", JobDto.From(run));
    }

    private static async Task<Accepted<JobDto>> StartDiagnostics(IJobQueue queue, ClaimsPrincipal user, CancellationToken ct)
    {
        var userId = user.UserId();
        var run = await queue.EnqueueAsync(DiagnosticsJob.JobKind, null, userId, null, ct);
        return TypedResults.Accepted($"/api/jobs/{run.Id}", JobDto.From(run));
    }
}
