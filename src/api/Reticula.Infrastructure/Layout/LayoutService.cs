using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Layout;
using Reticula.Domain.Projects;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Geo;

namespace Reticula.Infrastructure.Layout;

public sealed record ImportOutcome(CalcImportResult Result, ImportBatch? Batch);

/// <summary>Stores imported stands and buildings, links buildings to stands and keeps predictions current.</summary>
public sealed class LayoutService(ReticulaDbContext db, ICalcClient calc, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads the file through the calc service. With <paramref name="dryRun"/> nothing is stored.
    /// Otherwise, unless the file has errors, replaces the project's stands (or its not-yet-inspected
    /// buildings) with the file's features and refreshes building predictions.
    /// </summary>
    public async Task<ImportOutcome> ImportAsync(
        Project project, string kind, string fileName, byte[] data, string? sourceCrs, string? layer,
        bool dryRun, Guid userId, CancellationToken ct)
    {
        var area = JsonSerializer.Serialize(PolygonDto.From(project.Area), Json);
        var result = await calc.ImportAsync(new CalcImportRequest(new MemoryStream(data), fileName, kind, sourceCrs, layer, area), ct);
        if (dryRun || result.HasErrors) return new ImportOutcome(result, null);

        var now = time.GetUtcNow();
        var batch = new ImportBatch(Guid.CreateVersion7(), project.Id, kind, fileName, result.Format, result.SourceCrs,
            result.CrsReason, result.Features.Count, JsonSerializer.Serialize(result.Issues, Json),
            Convert.ToHexStringLower(SHA256.HashData(data)), userId, now);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ImportBatches.Add(batch);

        if (kind == ImportKinds.Stands)
        {
            await db.Buildings.Where(b => b.ProjectId == project.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.StandId, (Guid?)null), ct);
            await db.Stands.Where(s => s.ProjectId == project.Id).ExecuteDeleteAsync(ct);
            foreach (var f in result.Features)
            {
                if (!f.Geometry.TryToPolygon(out var polygon, out _)) continue;
                db.Stands.Add(new Stand(Guid.CreateVersion7(), project.Id, batch.Id, f.Ref, f.Erf, f.Zoning, polygon!, f.AreaM2,
                    JsonSerializer.Serialize(f.Attributes, Json)));
            }
        }
        else
        {
            // Buildings already seen on site are kept; only map-derived ones are replaced.
            await db.Buildings.Where(b => b.ProjectId == project.Id && b.Status == BuildingStatus.Predicted).ExecuteDeleteAsync(ct);
            foreach (var f in result.Features)
            {
                if (!f.Geometry.TryToPolygon(out var polygon, out _)) continue;
                db.Buildings.Add(new Building(Guid.CreateVersion7(), project.Id, batch.Id, f.Ref, f.OsmId, polygon!, f.AreaM2,
                    JsonSerializer.Serialize(f.Tags, Json), now));
            }
        }

        await db.SaveChangesAsync(ct);
        await RefreshBuildingsAsync(project, ct);
        await tx.CommitAsync(ct);
        return new ImportOutcome(result, batch);
    }

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
