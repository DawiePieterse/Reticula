using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Maps;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Infrastructure.Maps;

/// <summary>Builds the project's offline basemap: the project area plus a margin, cut from the map source by the calc service.</summary>
public sealed class MapPackJob(ReticulaDbContext db, ICalcClient calc, IFileStore store, TimeProvider time) : IJobHandler
{
    public const string JobKind = "map.pack";
    public string Kind => JobKind;

    /// <summary>About 500 m around the project area, so the map does not stop at its edge.</summary>
    public const double MarginDeg = 0.005;

    public async Task<object?> RunAsync(JobContext context, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == context.ProjectId && p.ArchivedAt == null, ct)
                      ?? throw new InvalidOperationException("The project no longer exists.");
        var box = project.Area.EnvelopeInternal.Copy();
        box.ExpandBy(MarginDeg);

        await context.Progress.ReportAsync(10, "Cutting the map for the project area", ct);
        var map = await calc.ExtractMapAsync(box.MinX, box.MinY, box.MaxX, box.MaxY, ct);

        await context.Progress.ReportAsync(80, "Saving the map", ct);
        var id = Guid.CreateVersion7();
        var key = $"projects/{project.Id:N}/maps/{id:N}.pmtiles";
        await store.SaveAsync(key, new MemoryStream(map.Data, writable: false), ct);

        // The project has one pack: the old row goes before the new one takes its place, in one transaction.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var previous = await db.MapPacks.FirstOrDefaultAsync(m => m.ProjectId == project.Id, ct);
            if (previous is not null)
            {
                db.MapPacks.Remove(previous);
                await db.SaveChangesAsync(ct);
            }
            db.MapPacks.Add(new MapPack(id, project.Id, key, map.Data.LongLength, Convert.ToHexStringLower(SHA256.HashData(map.Data)),
                box.MinX, box.MinY, box.MaxX, box.MaxY, map.MaxZoom, map.TileCount, map.Source, context.RequestedBy, time.GetUtcNow()));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            if (previous is not null) await store.DeleteAsync(previous.StorageKey, ct);
        }

        return new { packId = id, sizeBytes = map.Data.LongLength, tileCount = map.TileCount, maxZoom = map.MaxZoom, source = map.Source };
    }
}
