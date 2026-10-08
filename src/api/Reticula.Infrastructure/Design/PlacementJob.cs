using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Reticula.Domain.Design;
using Reticula.Domain.Field;
using Reticula.Domain.Layout;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Geo;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Infrastructure.Design;

/// <summary>
/// Pre-design placement (ADR 0010). Sends the project's roads, the loads of its standing buildings and the authority's connection point
/// to the calc service, then stores the proposal: the result on one row per project, and every proposed site and route as a candidate
/// with source "proposed", replacing the previous proposal's candidates. Candidates marked in the field are never touched.
/// </summary>
public sealed class PlacementJob(ReticulaDbContext db, ICalcClient calc, TimeProvider time) : IJobHandler
{
    public const string JobKind = "lv.placement";
    public string Kind => JobKind;

    public static readonly JsonSerializerOptions Json = LvNetworkService.Json;

    public async Task<object?> RunAsync(JobContext context, CancellationToken ct)
    {
        var project = (context.ProjectId is { } projectId ? await db.Projects.ActiveAsync(projectId, ct) : null)
                      ?? throw new InvalidOperationException("The project no longer exists.");

        await context.Progress.ReportAsync(5, "Reading roads and loads", ct);
        var roads = await db.Roads.AsNoTracking().Where(r => r.ProjectId == project.Id).OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.Geometry }).ToListAsync(ct);
        var roadInputs = roads.Select(r => new PlacementRoad(r.Id.ToString(), LineStringDto.From(r.Geometry).Coordinates)).ToList();

        // Loads of standing buildings. A building without a load estimate is left out and counted, never guessed.
        var rows = await (
            from b in db.Buildings.AsNoTracking()
            where b.ProjectId == project.Id && b.Status != BuildingStatus.NotPresent
            join s in db.Stands.AsNoTracking() on b.StandId equals s.Id into ss
            from s in ss.DefaultIfEmpty()
            join l in db.LoadPoints.AsNoTracking() on b.Id equals l.BuildingId into ls
            from l in ls.DefaultIfEmpty()
            orderby b.Id
            select new { b.Id, b.Location, Erf = s == null ? null : s.ErfNumber, Load = l }).ToListAsync(ct);
        var withLoad = rows.Where(x => x.Load is { Kva: > 0 }).ToList();
        var loadInputs = withLoad.Select(x => new PlacementLoad(x.Load!.Id.ToString(), [x.Location.X, x.Location.Y], x.Load.Kva, x.Load.Kind,
            x.Load.Kind == LoadKinds.Residential ? x.Load.ClassOverride ?? x.Load.Category : null, x.Erf)).ToList();

        // The authority's point of supply, when the existing network import carried one.
        var connection = await db.NetworkAssets.AsNoTracking()
            .Where(a => a.ProjectId == project.Id && a.AssetType == "connection_point").OrderBy(a => a.Id)
            .Select(a => a.Geometry).FirstOrDefaultAsync(ct);
        var cp = connection?.Centroid is { } centre ? new[] { centre.X, centre.Y } : null;

        await context.Progress.ReportAsync(15, $"Placing transformers for {loadInputs.Count} loads along {roadInputs.Count} roads", ct);
        var result = await calc.PlaceLvAsync(project.RulesRef, roadInputs, loadInputs, cp, ct);

        await context.Progress.ReportAsync(85, "Saving the proposal", ct);
        var now = time.GetUtcNow();
        var unplaced = result.Issues.Where(i => i.Code is "far_from_road" or "unassigned").Sum(i => i.Count);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var previous = await db.Candidates.Where(x => x.ProjectId == project.Id && x.Source == CandidateSources.Proposed && x.ArchivedAt == null).ToListAsync(ct);
            foreach (var old in previous) old.Archive(now);
            foreach (var c in result.Candidates)
            {
                var input = new GeometryInput(c.Geometry.GetProperty("type").GetString() ?? "", c.Geometry.GetProperty("coordinates"));
                if (!input.TryToGeometry(out var geometry, out var error)) throw new InvalidOperationException($"Calc service returned a bad {c.Kind} geometry: {error}");
                var note = c.Kind == CandidateKinds.Transformer && result.Transformers.FirstOrDefault(t => t.Id == c.Label) is { } t
                    ? $"Proposed {t.Id}: {t.RatingKva:0} kVA, {t.Loads} loads, {t.DemandKva:0.#} kVA at the confidence level"
                    : c.Label is null ? "Proposed" : $"Proposed, {c.Label}";
                db.Candidates.Add(new Candidate(Guid.CreateVersion7(), project.Id, c.Kind, geometry!, note, context.RequestedBy, now, CandidateSources.Proposed));
            }
            var existing = await db.Placements.FirstOrDefaultAsync(p => p.ProjectId == project.Id, ct);
            if (existing is not null) db.Placements.Remove(existing);
            db.Placements.Add(new Placement(Guid.CreateVersion7(), project.Id, result.RulesRef, result.RulesHash, result.Clause,
                JsonSerializer.Serialize(result, Json), result.Transformers.Count, loadInputs.Count, unplaced, context.RequestedBy, now));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        return new
        {
            transformers = result.Transformers.Count, loads = loadInputs.Count, buildingsWithoutLoad = rows.Count - withLoad.Count,
            unplaced, candidates = result.Candidates.Count, connectionPoint = cp is not null,
        };
    }
}
