using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Reticula.Api.Jobs;
using Reticula.Domain.Auth;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Jobs;
using Reticula.Infrastructure.Maps;

namespace Reticula.Api.Maps;

public sealed record TilePackDto(
    int MinZoom, int MaxZoom, int TileCount, long SizeBytes, string Sha256, string Source, string Attribution,
    double[] Bounds, DateTimeOffset BuiltAt);

public sealed record TileEstimate(int MinZoom, int MaxZoom, int Tiles);

/// <summary>What the client needs to offer an offline map: whether one can be built, the current pack, and what a new one would hold.</summary>
public sealed record TilePackStatus(bool SourceConfigured, TilePackDto? Pack, TileEstimate? Estimate, string? EstimateProblem);

public static class TilePackEndpoints
{
    public static IEndpointRouteBuilder MapTilePackEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{projectId:guid}/tile-pack").WithTags("Maps").RequireAuthorization(Policies.FieldUser);
        g.MapGet("/", Status);
        g.MapPost("/", Build);
        g.MapGet("/file", Download);
        return app;
    }

    private static async Task<Results<Ok<TilePackStatus>, NotFound>> Status(Guid projectId, ReticulaDbContext db, IConfiguration config, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.ArchivedAt == null, ct);
        if (project is null) return TypedResults.NotFound();
        var options = TileOptions.From(config);
        var pack = await db.TilePacks.AsNoTracking().FirstOrDefaultAsync(p => p.ProjectId == projectId, ct);
        TileEstimate? estimate = null;
        string? problem = null;
        try
        {
            var plan = TilePackPlanner.Plan(project.Area, options);
            estimate = new TileEstimate(plan.MinZoom, plan.MaxZoom, plan.TileCount);
        }
        catch (InvalidOperationException e)
        {
            problem = e.Message;
        }
        var dto = pack is null ? null : new TilePackDto(pack.MinZoom, pack.MaxZoom, pack.TileCount, pack.SizeBytes, pack.Sha256, pack.Source,
            pack.Attribution, [pack.West, pack.South, pack.East, pack.North], pack.BuiltAt);
        return TypedResults.Ok(new TilePackStatus(options.SourceUrl is not null, dto, estimate, problem));
    }

    private static async Task<Results<Accepted<JobDto>, NotFound, ProblemHttpResult>> Build(
        Guid projectId, ReticulaDbContext db, IConfiguration config, IJobQueue queue, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)) return TypedResults.NotFound();
        if (TileOptions.From(config).SourceUrl is null)
            return TypedResults.Problem("No offline tile source is configured. Set Tiles:SourceUrl to a tile server that allows offline use.", statusCode: 409);
        var running = await db.JobRuns.AsNoTracking().FirstOrDefaultAsync(j => j.ProjectId == projectId && j.Kind == TilePackJob.JobKind
            && (j.Status == Domain.Jobs.JobStatus.Queued || j.Status == Domain.Jobs.JobStatus.Running), ct);
        if (running is not null) return TypedResults.Accepted($"/api/jobs/{running.Id}", JobDto.From(running));

        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var run = await queue.EnqueueAsync(TilePackJob.JobKind, null, userId, projectId, ct);
        return TypedResults.Accepted($"/api/jobs/{run.Id}", JobDto.From(run));
    }

    private static async Task<IResult> Download(Guid projectId, ReticulaDbContext db, IFileStore files, CancellationToken ct)
    {
        var pack = await db.TilePacks.AsNoTracking().FirstOrDefaultAsync(p => p.ProjectId == projectId, ct);
        if (pack is null) return TypedResults.NotFound();
        var stream = await files.OpenReadAsync(pack.StorageKey, ct);
        if (stream is null) return TypedResults.NotFound();
        return TypedResults.File(stream, "application/vnd.pmtiles", $"{projectId:N}.pmtiles",
            entityTag: new EntityTagHeaderValue($"\"{pack.Sha256}\""), enableRangeProcessing: true);
    }
}
