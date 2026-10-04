using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Reticula.Domain.Field;
using Reticula.Domain.Layout;
using Reticula.Infrastructure.Data;

namespace Reticula.Infrastructure.Design;

/// <summary>Field data in the shape the calc service's design endpoints take (snake_case when serialised).</summary>
public sealed class DesignInputs(ReticulaDbContext db)
{
    public async Task<List<object>> RoutesAsync(Guid projectId, string kind, CancellationToken ct)
    {
        var routes = await db.Candidates.AsNoTracking().Where(c => c.ProjectId == projectId && c.ArchivedAt == null && c.Kind == kind).ToListAsync(ct);
        return [.. routes.Select(r => (object)new { Id = r.Id.ToString(), Coordinates = ((LineString)r.Geometry).Coordinates.Select(c => new[] { c.X, c.Y }) })];
    }

    /// <summary>Every building present with its load; throws if any has none.</summary>
    public async Task<List<object>> CustomersAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await (
            from b in db.Buildings.AsNoTracking()
            where b.ProjectId == projectId && b.Status != BuildingStatus.NotPresent
            join l in db.LoadPoints.AsNoTracking() on b.Id equals l.BuildingId into lps
            from l in lps.DefaultIfEmpty()
            join s in db.Stands.AsNoTracking() on b.StandId equals s.Id into stands
            from s in stands.DefaultIfEmpty()
            select new { b, l, Erf = s == null ? null : s.ErfNumber }).ToListAsync(ct);
        var missing = rows.Where(x => x.l is null).Select(x => x.Erf ?? x.b.Id.ToString()).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"{missing.Count} buildings have no load recorded (e.g. {string.Join(", ", missing.Take(5))}); record them first.");
        return [.. rows.Select(x => (object)new
        {
            BuildingId = x.b.Id.ToString(),
            Lon = x.b.Location.X,
            Lat = x.b.Location.Y,
            Phases = x.l!.Kind == LoadKinds.Residential ? x.l.Phases : 1,
            Kind = x.l.Kind,
            LoadClass = x.l.Kind == LoadKinds.Residential ? x.l.Category : null,
            SpecialKva = x.l.Kind == LoadKinds.Special ? x.l.Kva : (double?)null,
            Inspected = x.b.Status is BuildingStatus.Confirmed or BuildingStatus.New,
            Erf = x.Erf,
        })];
    }

    public async Task<List<IEnumerable<double[]>>> RoadsAsync(Guid projectId, CancellationToken ct)
    {
        var roads = await db.MapFeatures.AsNoTracking().Where(m => m.ProjectId == projectId && m.Layer == MapLayers.Roads).Select(m => m.Geometry).ToListAsync(ct);
        return [.. roads.OfType<LineString>().Select(l => l.Coordinates.Select(c => new[] { c.X, c.Y }))];
    }

    /// <summary>
    /// Where the MV network is fed from: the engineer's point if given; else the authority's connection point (imported
    /// network data) nearest the project; else the nearest point of an existing MV line; else the end of the MV routes
    /// furthest from the transformer sites. The note says which, so the design states its assumption.
    /// </summary>
    public async Task<(double[] Point, string? Note)> SupplyAsync(Guid projectId, double[]? given, IReadOnlyList<Point> sites, Polygon area,
        IReadOnlyList<LineString> mvRoutes, CancellationToken ct)
    {
        if (given is { Length: 2 }) return (given, null);
        var centre = area.Centroid;
        var assets = await db.MapFeatures.AsNoTracking().Where(m => m.ProjectId == projectId && m.Layer == MapLayers.Network).ToListAsync(ct);
        var cp = assets.Where(a => a.Subtype == "connection_point" && a.Geometry is Point).OrderBy(a => a.Geometry.Distance(centre)).FirstOrDefault();
        if (cp is not null)
            return ([((Point)cp.Geometry).X, ((Point)cp.Geometry).Y], $"Supply taken at the authority's connection point {cp.Name ?? cp.SourceRef} from the imported network data.");
        var mvLine = assets.Where(a => a.Subtype == "mv_line" && a.Geometry is LineString).OrderBy(a => a.Geometry.Distance(centre)).FirstOrDefault();
        var routeEnds = mvRoutes.SelectMany(r => new[] { r.StartPoint, r.EndPoint }).ToList();
        if (mvLine is not null && routeEnds.Count > 0)
        {
            var nearest = routeEnds.OrderBy(e => e.Distance(mvLine.Geometry)).First();
            var onLine = NetTopologySuite.Operation.Distance.DistanceOp.NearestPoints(mvLine.Geometry, nearest)[0];
            return ([onLine.X, onLine.Y], $"Supply taken on the existing MV line {mvLine.Name ?? mvLine.SourceRef} nearest the MV route.");
        }
        if (routeEnds.Count == 0) throw new InvalidOperationException("Mark the MV route on the field screen before designing the MV network.");
        var far = routeEnds.OrderByDescending(e => sites.Count == 0 ? 0 : sites.Min(s => s.Distance(e))).First();
        return ([far.X, far.Y], "No connection point is known yet (plan 4.1): supply assumed at the end of the MV route furthest from the transformer sites.");
    }
}
