using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Reticula.Domain.Design;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Geo;

namespace Reticula.Infrastructure.Review;

/// <summary>
/// The whole project in one zip (plan 7.5), readable without Reticula: layout and field data as GeoJSON, records as JSON, photos and
/// documents as files, the design runs that the project's design and revisions rest on with their exact requests and results, and the
/// audit trail.
/// </summary>
public sealed class ProjectExport(ReticulaDbContext db, IFileStore files, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <returns>A temporary file, deleted when the stream closes; null when the project does not exist.</returns>
    public async Task<(Stream Stream, string FileName)?> WriteAsync(Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.ActiveAsync(projectId, ct);
        if (project is null) return null;
        var path = Path.Combine(Path.GetTempPath(), $"reticula-export-{Guid.NewGuid():n}.zip");
        await using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
        {
            var now = time.GetUtcNow();
            await TextAsync(zip, "README.txt", $"""
                Reticula project export
                Project: {project.Name}
                Rules: {project.RulesRef}
                Exported: {now:yyyy-MM-dd HH:mm} UTC

                layout/       stands, buildings, roads, contours and the authority's network, GeoJSON (WGS84)
                field/        candidates (GeoJSON), inspections, load points, assumptions, photos
                design/       the design runs the current design and the revisions rest on: request.json is exactly what the calc
                              service was sent, result.json exactly what it returned; runs.json lists every run
                review/       revisions, report sections and the audit trail
                documents/    every current and every locked document, with documents.json saying what each was made from
                """, ct);

            await JsonAsync(zip, "project.json", new
            {
                project.Id, project.Name, project.RulesRef, Area = GeometryInput.ToDto(project.Area), project.CreatedAt, project.UpdatedAt,
                ConnectionPoint = await db.ConnectionPoints.AsNoTracking().Where(c => c.ProjectId == projectId)
                    .Select(c => new { Location = new[] { c.Location.X, c.Location.Y }, c.VoltageKv, c.CapacityKva, c.Fault3PhKa, c.Fault3PhMinKa,
                        c.Fault1PhKa, c.XOverR, c.Source, c.ReceivedOn, c.UpdatedAt }).FirstOrDefaultAsync(ct),
            }, ct);

            var stands = await db.Stands.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Id).ToListAsync(ct);
            await FeaturesAsync(zip, "layout/stands.geojson", stands, s => s.Geometry, s => new { s.Id, s.SourceRef, s.ErfNumber, s.Zoning, s.AreaM2 }, ct);
            var buildings = await db.Buildings.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Id).ToListAsync(ct);
            await FeaturesAsync(zip, "layout/buildings.geojson", buildings, b => b.Location, b => new
            {
                b.Id, b.SourceRef, b.OsmId, b.StandId, b.Zoning, b.AreaM2, b.PredictedType, b.PredictedConfidence, b.PredictionSource, b.LowConfidence,
                Status = b.Status.ToString().ToLowerInvariant(), b.ConfirmedType, b.InspectedAt, b.UpdatedAt,
            }, ct);
            var roads = await db.Roads.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Id).ToListAsync(ct);
            await FeaturesAsync(zip, "layout/roads.geojson", roads, r => r.Geometry, r => new { r.Id, r.SourceRef, r.OsmId, r.Name, r.RoadClass }, ct);
            var contours = await db.Contours.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Id).ToListAsync(ct);
            await FeaturesAsync(zip, "layout/contours.geojson", contours, c => c.Geometry, c => new { c.Id, c.SourceRef, c.ElevationM }, ct);
            var assets = await db.NetworkAssets.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Id).ToListAsync(ct);
            await FeaturesAsync(zip, "layout/network.geojson", assets, a => a.Geometry, a => new
            {
                a.Id, a.SourceRef, a.AssetType, a.Label, a.VoltageKv, a.RatingKva, a.CapacityKva, a.FaultLevelKa,
            }, ct);

            var candidates = await db.Candidates.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.CreatedAt).ToListAsync(ct);
            await FeaturesAsync(zip, "field/candidates.geojson", candidates, c => c.Geometry, c => new
            {
                c.Id, c.Kind, c.Source, c.Notes, c.CreatedBy, c.CreatedAt, c.UpdatedAt, c.ArchivedAt,
            }, ct);
            await JsonAsync(zip, "field/inspections.json", await db.Inspections.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.CapturedAt)
                .Select(i => new { i.Id, i.Action, i.BuildingId, i.CandidateId, i.Value, Position = i.Position == null ? null : new[] { i.Position.X, i.Position.Y },
                    i.Notes, i.InspectorId, i.CapturedAt }).ToListAsync(ct), ct);
            await JsonAsync(zip, "field/load-points.json", await db.LoadPoints.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Id).ToListAsync(ct), ct);
            await JsonAsync(zip, "field/assumptions.json", await db.Assumptions.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.CreatedAt).ToListAsync(ct), ct);
            var photos = await db.Photos.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.CapturedAt).ToListAsync(ct);
            await JsonAsync(zip, "field/photos.json", photos.Select(p => new { p.Id, p.BuildingId, p.CandidateId, p.InspectionId, p.ContentType, p.SizeBytes,
                p.Sha256, p.CapturedAt, File = $"field/photos/{p.Id:n}{Ext(p.ContentType)}" }), ct);
            foreach (var p in photos) await FileAsync(zip, $"field/photos/{p.Id:n}{Ext(p.ContentType)}", p.StorageKey, ct);

            var runs = await db.DesignRuns.AsNoTracking().Where(r => r.ProjectId == projectId).OrderBy(r => r.Number)
                .Select(r => new { r.Id, r.Number, r.Mode, r.ParentRunId, r.RulesRef, r.RulesHash, r.InputsHash, r.ResultHash, r.Construction, r.FitToSubmit,
                    r.Checks, r.Failures, r.Capex, r.Lifetime, r.CreatedBy, r.CreatedAt }).ToListAsync(ct);
            await JsonAsync(zip, "design/runs.json", runs, ct);
            var revisions = await db.Revisions.AsNoTracking().Where(r => r.ProjectId == projectId).OrderBy(r => r.Number).ToListAsync(ct);
            var keep = revisions.Select(r => r.DesignRunId).ToHashSet();
            if (await DesignRuns.CurrentAsync(db, projectId, ct) is { } current) keep.Add(current.Id);
            foreach (var id in keep)
            {
                var r = await db.DesignRuns.AsNoTracking().FirstAsync(x => x.Id == id, ct);
                await TextAsync(zip, $"design/run-{r.Number}/request.json", r.RequestJson, ct);
                if (r.ResultJson is not null) await TextAsync(zip, $"design/run-{r.Number}/result.json", r.ResultJson, ct);
            }

            await JsonAsync(zip, "review/revisions.json", revisions, ct);
            await JsonAsync(zip, "review/report-sections.json", await db.ReportSections.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Order).ToListAsync(ct), ct);
            await JsonAsync(zip, "review/audit.json", await db.AuditEntries.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.At).ToListAsync(ct), ct);

            var documents = await db.Documents.AsNoTracking().Where(d => d.ProjectId == projectId && (d.SupersededAt == null || d.Locked))
                .OrderBy(d => d.RevisionNumber).ThenBy(d => d.Number).ToListAsync(ct);
            await JsonAsync(zip, "documents/documents.json", documents, ct);
            foreach (var d in documents) await FileAsync(zip, $"documents/R{d.RevisionNumber}/{d.FileName}", d.StorageKey, ct);
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        var slug = new string([.. project.Name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        return (stream, $"{(slug.Length == 0 ? "project" : slug)}-export-{time.GetUtcNow():yyyyMMdd}.zip");
    }

    private static string Ext(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/heic" => ".heic",
        _ => ".bin",
    };

    private static async Task TextAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        await using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await s.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
    }

    private static async Task JsonAsync<T>(ZipArchive zip, string name, T value, CancellationToken ct)
    {
        await using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await JsonSerializer.SerializeAsync(s, value, Json, ct);
    }

    private static Task FeaturesAsync<T>(ZipArchive zip, string name, IEnumerable<T> rows, Func<T, Geometry> geometry, Func<T, object> props,
        CancellationToken ct) =>
        JsonAsync(zip, name, new
        {
            type = "FeatureCollection",
            features = rows.Select(r => new { type = "Feature", geometry = GeometryInput.ToDto(geometry(r)), properties = props(r) }),
        }, ct);

    private async Task FileAsync(ZipArchive zip, string name, string key, CancellationToken ct)
    {
        await using var src = await files.OpenReadAsync(key, ct);
        if (src is null) return;
        await using var s = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
        await src.CopyToAsync(s, ct);
    }
}
