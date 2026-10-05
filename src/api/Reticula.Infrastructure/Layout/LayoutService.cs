using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Layout;
using Reticula.Domain.Projects;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using NetTopologySuite.Geometries;
using Reticula.Infrastructure.Geo;

namespace Reticula.Infrastructure.Layout;

public sealed record ImportOutcome(CalcImportResult Result, ImportBatch? Batch);

/// <summary>Stores imported stands and buildings, links buildings to stands and keeps predictions current.</summary>
public sealed class LayoutService(ReticulaDbContext db, ICalcClient calc, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads the file through the calc service. With <paramref name="dryRun"/> nothing is stored. Otherwise, unless the
    /// file has errors, replaces the project's features of that kind with the file's (for buildings, only those not yet
    /// inspected) and refreshes building predictions.
    /// </summary>
    public async Task<ImportOutcome> ImportAsync(
        Project project, string kind, string fileName, byte[] data, string? sourceCrs, string? layer,
        bool dryRun, Guid userId, CancellationToken ct)
    {
        var result = await calc.ImportAsync(new CalcImportRequest(new MemoryStream(data), fileName, kind, sourceCrs, layer, AreaJson(project)), ct);
        if (dryRun || result.HasErrors) return new ImportOutcome(result, null);
        return new ImportOutcome(result, await StoreAsync(project, kind, fileName, Convert.ToHexStringLower(SHA256.HashData(data)), result, userId, ct));
    }

    /// <summary>As <see cref="ImportAsync"/>, with buildings or roads fetched from OpenStreetMap for the project area.</summary>
    public async Task<ImportOutcome> ImportOsmAsync(Project project, string kind, bool dryRun, Guid userId, CancellationToken ct)
    {
        var result = await calc.ImportOsmAsync(kind, AreaJson(project), ct);
        if (dryRun || result.HasErrors) return new ImportOutcome(result, null);
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(result.Features, Json)));
        return new ImportOutcome(result, await StoreAsync(project, kind, OsmFileName, hash, result, userId, ct));
    }

    public const string OsmFileName = "OpenStreetMap (Overpass)";

    private async Task<ImportBatch> StoreAsync(Project project, string kind, string fileName, string sha256, CalcImportResult result, Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var batch = new ImportBatch(Guid.CreateVersion7(), project.Id, kind, fileName, result.Format, result.SourceCrs,
            result.CrsReason, result.Features.Count, JsonSerializer.Serialize(result.Issues, Json), sha256, userId, now);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ImportBatches.Add(batch);
        string Attributes(CalcFeature f) => JsonSerializer.Serialize(f.Attributes, Json);

        switch (kind)
        {
            case ImportKinds.Stands:
                await db.Buildings.Where(b => b.ProjectId == project.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.StandId, (Guid?)null), ct);
                await db.Stands.Where(s => s.ProjectId == project.Id).ExecuteDeleteAsync(ct);
                foreach (var f in result.Features)
                    if (f.Geometry.TryToPolygon(out var polygon, out _))
                        db.Stands.Add(new Stand(Guid.CreateVersion7(), project.Id, batch.Id, f.Ref, f.Erf, f.Zoning, polygon!, f.AreaM2, Attributes(f)));
                break;
            case ImportKinds.Buildings:
                // Buildings already seen on site are kept; only map-derived ones are replaced.
                await db.Buildings.Where(b => b.ProjectId == project.Id && b.Status == BuildingStatus.Predicted).ExecuteDeleteAsync(ct);
                foreach (var f in result.Features)
                    if (f.Geometry.TryToPolygon(out var polygon, out _))
                        db.Buildings.Add(new Building(Guid.CreateVersion7(), project.Id, batch.Id, f.Ref, f.OsmId, polygon!, f.AreaM2,
                            JsonSerializer.Serialize(f.Tags, Json), now));
                break;
            case ImportKinds.Roads:
                await db.Roads.Where(r => r.ProjectId == project.Id).ExecuteDeleteAsync(ct);
                foreach (var f in result.Features)
                    if (f.Geometry.ToGeometry() is LineString line)
                        db.Roads.Add(new Road(Guid.CreateVersion7(), project.Id, batch.Id, f.Ref, f.OsmId, Clip(f.Name, 200), Clip(f.Category, 50), line, f.LengthM, Attributes(f)));
                break;
            case ImportKinds.Contours:
                await db.Contours.Where(c => c.ProjectId == project.Id).ExecuteDeleteAsync(ct);
                foreach (var f in result.Features)
                    if (f.Geometry.ToGeometry() is LineString line && f.ElevationM is { } z)
                        db.Contours.Add(new Contour(Guid.CreateVersion7(), project.Id, batch.Id, f.Ref, z, line));
                break;
            case ImportKinds.Network:
                await db.NetworkAssets.Where(a => a.ProjectId == project.Id).ExecuteDeleteAsync(ct);
                foreach (var f in result.Features)
                    if (f.Geometry.ToGeometry() is Point or LineString)
                        db.NetworkAssets.Add(new NetworkAsset(Guid.CreateVersion7(), project.Id, batch.Id, f.Ref, Clip(f.Category, 30) ?? "other",
                            Clip(f.Name, 100), f.Geometry.ToGeometry()!, f.VoltageKv, f.RatingKva, f.CapacityKva, f.FaultLevelKa,
                            JsonSerializer.Serialize(f.Missing ?? [], Json), Attributes(f)));
                break;
            default:
                throw new ArgumentException($"Unknown import kind '{kind}'.", nameof(kind));
        }

        await db.SaveChangesAsync(ct);
        if (kind is ImportKinds.Stands or ImportKinds.Buildings) await RefreshBuildingsAsync(project, ct);
        await tx.CommitAsync(ct);
        return batch;
    }

    private static string AreaJson(Project project) => JsonSerializer.Serialize(PolygonDto.From(project.Area), Json);

    private static string? Clip(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;

    /// <summary>Links each building to the stand it mostly sits on, copies that stand's zoning, and re-predicts types.</summary>
    public async Task RefreshBuildingsAsync(Project project, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE buildings b SET stand_id = m.stand_id, zoning = m.zoning
            FROM (
                SELECT DISTINCT ON (b2.id) b2.id AS building_id, s.id AS stand_id, s.zoning
                FROM buildings b2
                JOIN stands s ON s.project_id = b2.project_id AND ST_Intersects(s.geometry, COALESCE(b2.footprint, b2.location))
                WHERE b2.project_id = {project.Id}
                ORDER BY b2.id, ST_Area(ST_Intersection(s.geometry, COALESCE(b2.footprint, b2.location))) DESC
            ) m
            WHERE b.id = m.building_id
            """, ct);

        // The SQL above bypassed the change tracker; read fresh rows.
        db.ChangeTracker.Clear();
        var buildings = await db.Buildings.Where(b => b.ProjectId == project.Id && b.Status == BuildingStatus.Predicted).ToListAsync(ct);
        if (buildings.Count == 0) return;

        var inputs = buildings.Select(b => new BuildingPredictionInput(
            b.Id.ToString(), b.AreaM2, JsonSerializer.Deserialize<Dictionary<string, string>>(b.TagsJson, Json) ?? [], b.Zoning)).ToList();
        var result = await calc.PredictBuildingTypesAsync(project.RulesRef, inputs, ct);

        var byId = result.Predictions.ToDictionary(p => p.Id);
        var now = time.GetUtcNow();
        foreach (var b in buildings)
        {
            if (!byId.TryGetValue(b.Id.ToString(), out var p)) continue;
            b.SetPrediction(p.Type, p.Confidence, p.Source, p.LowConfidence, JsonSerializer.Serialize(p.Signals, Json), result.RulesHash, now);
        }
        await db.SaveChangesAsync(ct);
    }
}
