using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Reticula.Api.Jobs;
using Reticula.Domain.Auth;
using Reticula.Domain.Jobs;
using Reticula.Domain.Maps;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Jobs;
using Reticula.Infrastructure.Maps;

namespace Reticula.Api.Maps;

public sealed record MapPackDto(Guid Id, long SizeBytes, string Sha256, double[] Bbox, int MaxZoom, int TileCount, string Source, DateTimeOffset BuiltAt)
{
    public static MapPackDto From(MapPack m) => new(m.Id, m.SizeBytes, m.Sha256, [m.MinLon, m.MinLat, m.MaxLon, m.MaxLat], m.MaxZoom, m.TileCount, m.Source, m.BuiltAt);
}

/// <param name="Pack">The current pack, if one was built.</param>
/// <param name="Job">The latest build started after it: running, or failed with the reason.</param>
public sealed record MapPackStatus(MapPackDto? Pack, JobDto? Job);

/// <summary>Offline basemap packs per project (plan item 1.9).</summary>
public static class MapPackEndpoints
{
    public static IEndpointRouteBuilder MapMapPackEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{projectId:guid}/map-pack").WithTags("Maps").RequireAuthorization(Policies.FieldUser);
        g.MapGet("/", Status);
        g.MapPost("/", Build);
        g.MapGet("/{packId:guid}.pmtiles", Download);
        return app;
    }

    private static async Task<Results<Ok<MapPackStatus>, NotFound>> Status(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)) return TypedResults.NotFound();
        var pack = await db.MapPacks.AsNoTracking().FirstOrDefaultAsync(m => m.ProjectId == projectId, ct);
        var job = await LatestJobAsync(db, projectId, ct);
        if (job is not null && pack is not null && job.CreatedAt <= pack.BuiltAt && job.IsFinished) job = null;
        return TypedResults.Ok(new MapPackStatus(pack is null ? null : MapPackDto.From(pack), job is null ? null : JobDto.From(job)));
    }

    /// <summary>Starts building the pack, or returns the build already under way.</summary>
    private static async Task<Results<Accepted<JobDto>, NotFound>> Build(Guid projectId, ReticulaDbContext db, IJobQueue queue, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)) return TypedResults.NotFound();
        var running = await LatestJobAsync(db, projectId, ct);
        var run = running is { IsFinished: false }
            ? running
            : await queue.EnqueueAsync(MapPackJob.JobKind, null, Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), projectId, ct);
        return TypedResults.Accepted($"/api/jobs/{run.Id}", JobDto.From(run));
    }

    /// <summary>The pack file. Supports range requests, so a map can also read it in place.</summary>
    private static async Task<Results<FileStreamHttpResult, NotFound>> Download(Guid projectId, Guid packId, ReticulaDbContext db, IFileStore store, CancellationToken ct)
    {
        var pack = await db.MapPacks.AsNoTracking().FirstOrDefaultAsync(m => m.Id == packId && m.ProjectId == projectId, ct);
        if (pack is null) return TypedResults.NotFound();
        var stream = await store.OpenReadAsync(pack.StorageKey, ct);
        if (stream is null) return TypedResults.NotFound();
        return TypedResults.File(stream, "application/vnd.pmtiles", $"{packId:N}.pmtiles", pack.BuiltAt,
            new EntityTagHeaderValue($"\"{pack.Sha256}\""), enableRangeProcessing: true);
    }

    private static Task<JobRun?> LatestJobAsync(ReticulaDbContext db, Guid projectId, CancellationToken ct) =>
        db.JobRuns.AsNoTracking().Where(j => j.ProjectId == projectId && j.Kind == MapPackJob.JobKind)
            .OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
}
