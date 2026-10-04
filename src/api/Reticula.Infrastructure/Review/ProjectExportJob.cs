using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Reticula.Domain.Review;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Infrastructure.Review;

/// <summary>
/// Exports a whole project in open formats (plan 7.5): field data as GeoJSON (WGS84), records as JSON, every design
/// run with its input and result, the documents, photos and the audit trail, zipped with a README describing the layout.
/// </summary>
public sealed class ProjectExportJob(ReticulaDbContext db, IFileStore files, TimeProvider time) : IJobHandler
{
    public const string JobKind = "project.export";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Kind => JobKind;

    public async Task<object?> RunAsync(JobContext context, CancellationToken ct)
    {
        var export = await db.Set<ProjectExport>().FirstAsync(e => e.Id == context.Payload.GetProperty("exportId").GetGuid(), ct);
        export.Start();
        await db.SaveChangesAsync(ct);
        try
        {
            var pid = export.ProjectId;
            var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == pid, ct);
            var tmp = Path.GetTempFileName();
            try
            {
                await using (var fs = File.Create(tmp))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    await context.Progress.ReportAsync(10, "Field data", ct);
                    await Entry(zip, "README.txt", Readme(project.Name, time.GetUtcNow()));
                    var cp = await db.ConnectionPoints.AsNoTracking().FirstOrDefaultAsync(c => c.ProjectId == pid, ct);
                    var rateList = project.RateListId is { } rl ? await db.RateLists.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rl, ct) : null;
                    await Json_(zip, "project.json", new
                    {
                        project.Id, project.Name, project.RulesRef, project.CreatedAt, project.UpdatedAt, Area = Geo(project.Area),
                        ConnectionPoint = cp is null ? null : new { Location = Geo(cp.Location), cp.VoltageKv, cp.AvailableCapacityKva, cp.FaultMvaMax, cp.FaultMvaMin, cp.XR, cp.SendingVoltagePct, cp.Reference, cp.UpdatedAt },
                        RateList = rateList is null ? null : new { rateList.Name, rateList.BasedOn, rateList.RateDate, rateList.Revision, Content = JsonNode.Parse(rateList.ContentJson), Overrides = JsonNode.Parse(rateList.OverridesJson) },
                    });
                    var stands = await db.Stands.AsNoTracking().Where(s => s.ProjectId == pid).ToListAsync(ct);
                    await Json_(zip, "field/stands.geojson", Collection(stands.Select(s => (Geo(s.Geometry), (object)new { s.Id, s.ErfNumber, s.Zoning, s.AreaM2, s.SourceRef, Attributes = JsonNode.Parse(s.AttributesJson) }))));
                    var loads = await db.LoadPoints.AsNoTracking().Where(l => l.ProjectId == pid).ToDictionaryAsync(l => l.BuildingId, ct);
                    var buildings = await db.Buildings.AsNoTracking().Where(b => b.ProjectId == pid).ToListAsync(ct);
                    await Json_(zip, "field/buildings.geojson", Collection(buildings.Select(b =>
                    {
                        loads.TryGetValue(b.Id, out var l);
                        return (Geo((Geometry?)b.Footprint ?? b.Location), (object)new
                        {
                            b.Id, b.StandId, Status = b.Status.ToString(), b.PredictedType, b.ConfirmedType, b.AreaM2, b.OsmId, Location = Geo(b.Location),
                            Load = l is null ? null : new { l.Kind, l.SpecialLoad, l.Category, l.IncomeBand, l.ClassOverride, l.EstimatedKva, l.Kva, l.Overridden, l.OverrideReason, l.Phases,
                                Status = l.Status.ToString(), Observations = JsonNode.Parse(l.ObservationsJson), Trace = JsonNode.Parse(l.TraceJson), l.RulesHash, l.UpdatedAt },
                        });
                    })));
                    var candidates = await db.Candidates.AsNoTracking().Where(c => c.ProjectId == pid).ToListAsync(ct);
                    await Json_(zip, "field/candidates.geojson", Collection(candidates.Select(c => (Geo(c.Geometry), (object)new { c.Id, c.Kind, c.Notes, c.CreatedAt, c.ArchivedAt }))));
                    var features = await db.MapFeatures.AsNoTracking().Where(m => m.ProjectId == pid).ToListAsync(ct);
                    await Json_(zip, "field/map-features.geojson", Collection(features.Select(m => (Geo(m.Geometry), (object)new { m.Id, m.Layer, m.Subtype, m.Name, m.ElevationM, m.SourceRef, Attributes = JsonNode.Parse(m.AttributesJson) }))));
                    await Json_(zip, "field/inspections.json", await db.Inspections.AsNoTracking().Where(i => i.ProjectId == pid).OrderBy(i => i.RecordedAt)
                        .Select(i => new { i.Id, i.Action, i.BuildingId, i.CandidateId, i.Value, Position = i.Position == null ? null : new[] { i.Position.X, i.Position.Y }, i.AccuracyM, i.CapturedAt, i.RecordedAt, i.Notes, i.InspectorId }).ToListAsync(ct));
                    await Json_(zip, "assumptions.json", await db.Assumptions.AsNoTracking().Where(a => a.ProjectId == pid).OrderBy(a => a.CreatedAt)
                        .Select(a => new { a.Id, a.SubjectType, a.SubjectId, a.Code, a.Text, Status = a.Status.ToString(), a.CreatedAt, a.ClearedBy, a.ClearedAt, a.ClearNote }).ToListAsync(ct));

                    await context.Progress.ReportAsync(40, "Design runs", ct);
                    foreach (var r in await db.DesignRuns.AsNoTracking().Where(r => r.ProjectId == pid).OrderBy(r => r.CreatedAt).ToListAsync(ct))
                        await Json_(zip, $"design-runs/{r.CreatedAt:yyyyMMdd-HHmmss}-{r.Kind}-{r.Id:N}.json", new
                        {
                            r.Id, r.Kind, Status = r.Status.ToString(), r.RulesRef, r.RulesHash, r.Passed, r.Error, r.CreatedBy, r.CreatedAt, r.FinishedAt,
                            Parameters = JsonNode.Parse(r.ParametersJson), Input = r.InputJson is null ? null : JsonNode.Parse(r.InputJson), Result = r.ResultJson is null ? null : JsonNode.Parse(r.ResultJson),
                        });

                    await context.Progress.ReportAsync(60, "Documents and photos", ct);
                    var sets = await db.DocumentSets.AsNoTracking().Where(s => s.ProjectId == pid).OrderBy(s => s.Number).ToListAsync(ct);
                    var revisions = await db.Revisions.AsNoTracking().Where(r => r.ProjectId == pid).OrderBy(r => r.Number).ToListAsync(ct);
                    await Json_(zip, "revisions.json", new
                    {
                        Revisions = revisions.Select(r => new { r.Label, Status = r.Status.ToString(), r.SignedOffName, r.RegistrationNumber, r.SignedOffAt, r.Notes, r.SnapshotSha256, r.Reproduced, r.ReproducedAt }),
                        DocumentSets = sets.Select(s => new { s.Revision, Status = s.Status.ToString(), s.Locked, s.Engineer, s.SignedOff, s.CreatedAt, Sources = JsonNode.Parse(s.SourcesJson) }),
                    });
                    foreach (var d in await db.ProjectDocuments.AsNoTracking().Where(d => d.ProjectId == pid).ToListAsync(ct))
                    {
                        var setLabel = sets.FirstOrDefault(s => s.Id == d.SetId)?.Revision ?? "unknown";
                        await Copy(zip, $"documents/{setLabel}/{d.FileName}", d.StorageKey, ct);
                    }
                    foreach (var r in revisions.Where(r => r.SnapshotKey is not null))
                        await Copy(zip, $"revisions/{r.Label}/snapshot.json", r.SnapshotKey!, ct);
                    foreach (var p in await db.Photos.AsNoTracking().Where(p => p.ProjectId == pid).ToListAsync(ct))
                        await Copy(zip, $"photos/{p.Id:N}{(p.ContentType == "image/png" ? ".png" : ".jpg")}", p.StorageKey, ct);

                    await context.Progress.ReportAsync(85, "Audit trail", ct);
                    await Json_(zip, "audit.json", await db.AuditEntries.AsNoTracking().Where(a => a.ProjectId == pid).OrderBy(a => a.At)
                        .Select(a => new { a.At, a.UserId, a.EntityType, a.EntityId, a.Action, a.ChangesJson }).ToListAsync(ct));
                }
                var name = $"{Slug(project.Name)}-export-{time.GetUtcNow():yyyyMMdd-HHmm}.zip";
                var key = $"projects/{pid:N}/exports/{export.Id:N}.zip";
                string sha;
                long size;
                await using (var fs = File.OpenRead(tmp))
                {
                    size = fs.Length;
                    sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
                    fs.Position = 0;
                    await files.SaveAsync(key, fs, ct);
                }
                export.Succeed(key, name, size, sha, time.GetUtcNow());
                await db.SaveChangesAsync(ct);
                return new { exportId = export.Id, size };
            }
            finally
            {
                File.Delete(tmp);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            export.Fail("The export could not be made; see the job log.", time.GetUtcNow());
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private static string Readme(string project, DateTimeOffset now) => $"""
        {project} – Reticula project export ({now:yyyy-MM-dd HH:mm} UTC)

        Open formats only. Coordinates are WGS84 longitude, latitude.
          project.json                 project, connection point, rate list (content and dated overrides)
          field/stands.geojson         stand boundaries with erf numbers
          field/buildings.geojson      buildings with inspection status and their load (ADMD, class, observations, trace)
          field/candidates.geojson     transformer sites, LV and MV routes marked on site
          field/map-features.geojson   imported roads, contours and network assets
          field/inspections.json       every field action with position and time
          assumptions.json             the assumptions register with clearances and acceptances
          design-runs/*.json           every design run: parameters, the exact input sent to the calc service, the full result
          documents/<revision>/        generated documents (drawings, report, BoQ, GIS, registers, submission pack)
          revisions.json, revisions/   issued revisions, sign-offs and their snapshots
          photos/                      site photos
          audit.json                   who changed what, when, before and after

        """;

    private static async Task Entry(ZipArchive zip, string name, string text)
    {
        await using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await s.WriteAsync(Encoding.UTF8.GetBytes(text));
    }

    private static async Task Json_(ZipArchive zip, string name, object value)
    {
        await using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await JsonSerializer.SerializeAsync(s, value, Json);
    }

    private async Task Copy(ZipArchive zip, string name, string key, CancellationToken ct)
    {
        await using var src = await files.OpenReadAsync(key, ct);
        if (src is null) return;
        await using var dst = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await src.CopyToAsync(dst, ct);
    }

    private static object Collection(IEnumerable<(JsonNode? Geometry, object Properties)> features) => new
    {
        type = "FeatureCollection",
        features = features.Select(f => new { type = "Feature", geometry = f.Geometry, properties = f.Properties }),
    };

    /// <summary>GeoJSON geometry for the types the project stores.</summary>
    public static JsonNode? Geo(Geometry? g) => g switch
    {
        null => null,
        Point p => new JsonObject { ["type"] = "Point", ["coordinates"] = Pos(p.Coordinate) },
        LineString l => new JsonObject { ["type"] = "LineString", ["coordinates"] = Ring(l.Coordinates) },
        Polygon p => new JsonObject { ["type"] = "Polygon", ["coordinates"] = Rings(p) },
        MultiPolygon mp => new JsonObject { ["type"] = "MultiPolygon", ["coordinates"] = new JsonArray([.. mp.Geometries.Cast<Polygon>().Select(p => (JsonNode)Rings(p))]) },
        MultiLineString ml => new JsonObject { ["type"] = "MultiLineString", ["coordinates"] = new JsonArray([.. ml.Geometries.Select(l => (JsonNode)Ring(l.Coordinates))]) },
        MultiPoint mp => new JsonObject { ["type"] = "MultiPoint", ["coordinates"] = Ring(mp.Coordinates) },
        _ => null,
    };

    private static JsonArray Pos(Coordinate c) => new(c.X, c.Y);
    private static JsonArray Ring(Coordinate[] cs) => new([.. cs.Select(c => (JsonNode)Pos(c))]);
    private static JsonArray Rings(Polygon p) => new([.. new[] { p.ExteriorRing }.Concat(p.InteriorRings).Select(r => (JsonNode)Ring(r.Coordinates))]);

    private static string Slug(string s) => new string([.. s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-') is { Length: > 0 } x ? x[..Math.Min(x.Length, 40)] : "project";
}
