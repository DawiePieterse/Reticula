using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reticula.Domain.Maps;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Infrastructure.Maps;

/// <summary>Builds a project's offline base map: downloads the planned tiles and packs them into one PMTiles file.</summary>
public sealed class TilePackJob(ReticulaDbContext db, IHttpClientFactory http, IFileStore files, IConfiguration config, TimeProvider time) : IJobHandler
{
    public const string JobKind = "maps.tile-pack";
    public const string HttpClientName = "tiles";
    public string Kind => JobKind;

    public async Task<object?> RunAsync(JobContext context, CancellationToken ct)
    {
        var options = TileOptions.From(config);
        if (options.SourceUrl is null) throw new InvalidOperationException("No offline tile source is configured (Tiles:SourceUrl).");
        var projectId = context.ProjectId ?? throw new InvalidOperationException("The tile pack job needs a project.");
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)
                      ?? throw new InvalidOperationException("The project does not exist.");

        await context.Progress.ReportAsync(2, "Planning tiles", ct);
        var plan = TilePackPlanner.Plan(project.Area, options);

        var client = http.CreateClient(HttpClientName);
        var fetched = new ConcurrentBag<(int Z, int X, int Y, byte[] Data)>();
        var missing = 0;
        var failed = 0;
        var done = 0;
        await Parallel.ForEachAsync(plan.Tiles, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Concurrency), CancellationToken = ct },
            async (t, token) =>
            {
                var url = options.SourceUrl.Replace("{z}", t.Z.ToString()).Replace("{x}", t.X.ToString()).Replace("{y}", t.Y.ToString());
                var bytes = await FetchAsync(client, url, token);
                if (bytes is null) Interlocked.Increment(ref missing);
                else if (bytes.Length == 0) Interlocked.Increment(ref failed);
                else fetched.Add((t.Z, t.X, t.Y, bytes));
                var n = Interlocked.Increment(ref done);
                if (n % 50 == 0 || n == plan.TileCount)
                    await context.Progress.ReportAsync(5 + 85 * n / plan.TileCount, $"Downloaded {n} of {plan.TileCount} tiles", token);
            });

        if (failed > plan.TileCount / 20)
            throw new InvalidOperationException($"{failed} of {plan.TileCount} tiles could not be downloaded from the tile server.");
        if (fetched.IsEmpty) throw new InvalidOperationException("The tile server returned no tiles for the project area.");

        await context.Progress.ReportAsync(92, "Packing tiles", ct);
        var first = fetched.First().Data;
        var type = TypeOf(first);
        var metadata = JsonSerializer.Serialize(new
        {
            name = project.Name, description = "Reticula offline base map", attribution = options.Attribution, type = "baselayer",
            format = type.ToString().ToLowerInvariant(), source = options.SourceLabel,
        });
        using var archive = new MemoryStream();
        PmTilesWriter.Write(archive, fetched, new PmTilesInfo(type, plan.MinZoom, plan.MaxZoom, plan.West, plan.South, plan.East, plan.North, metadata));
        archive.Position = 0;
        var sha = Convert.ToHexStringLower(SHA256.HashData(archive));
        archive.Position = 0;
        var key = $"projects/{projectId:N}/tiles/{sha[..16]}.pmtiles";
        await files.SaveAsync(key, archive, ct);

        var now = time.GetUtcNow();
        var pack = await db.TilePacks.FirstOrDefaultAsync(p => p.ProjectId == projectId, ct);
        if (pack is null)
            db.TilePacks.Add(new TilePack(projectId, key, plan.MinZoom, plan.MaxZoom, fetched.Count, archive.Length, sha, options.SourceLabel, options.Attribution,
                plan.West, plan.South, plan.East, plan.North, context.RequestedBy, now));
        else
            pack.Replace(key, plan.MinZoom, plan.MaxZoom, fetched.Count, archive.Length, sha, options.SourceLabel, options.Attribution,
                plan.West, plan.South, plan.East, plan.North, context.RequestedBy, now);
        await db.SaveChangesAsync(ct);

        return new { tiles = fetched.Count, missing, failed, plan.MinZoom, plan.MaxZoom, sizeBytes = archive.Length, sha256 = sha };
    }

    /// <returns>The tile; null when the server has no tile there (404); empty after repeated failures.</returns>
    private static async Task<byte[]?> FetchAsync(HttpClient client, string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var resp = await client.GetAsync(url, ct);
                if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
                if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            await Task.Delay(200 * (attempt + 1), ct);
        }
        return [];
    }

    private static PmTileType TypeOf(byte[] b) => b switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => PmTileType.Png,
        [0xFF, 0xD8, ..] => PmTileType.Jpeg,
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => PmTileType.Webp,
        _ => PmTileType.Unknown,
    };
}

public static class TileServiceCollectionExtensions
{
    public static IServiceCollection AddTilePacks(this IServiceCollection services)
    {
        services.AddHttpClient(TilePackJob.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Reticula/0.1 (offline field maps)");
        });
        services.AddJobHandler<TilePackJob>();
        return services;
    }
}
