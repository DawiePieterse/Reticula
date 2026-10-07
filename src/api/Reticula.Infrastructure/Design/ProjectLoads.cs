using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Field;
using Reticula.Domain.Layout;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;

namespace Reticula.Infrastructure.Design;

/// <summary>The loads the LV network and the design run connect: one per building still standing, in a stable order.</summary>
public static class ProjectLoads
{
    /// <summary>
    /// Every building still standing, with its load when it has one, and each residential load's Herman-Beta class by load id.
    /// Buildings without a load are passed with no kVA, so the calc service reports them; they are never guessed.
    /// </summary>
    public static async Task<(List<LvLoadIn> Inputs, Dictionary<string, string?> Classes)> ReadAsync(ReticulaDbContext db, Guid projectId, CancellationToken ct)
    {
        var rows = await (
            from b in db.Buildings.AsNoTracking()
            where b.ProjectId == projectId && b.Status != BuildingStatus.NotPresent
            join s in db.Stands.AsNoTracking() on b.StandId equals s.Id into ss
            from s in ss.DefaultIfEmpty()
            join l in db.LoadPoints.AsNoTracking() on b.Id equals l.BuildingId into ls
            from l in ls.DefaultIfEmpty()
            orderby b.Id
            select new { b.Id, b.Location, Erf = s == null ? null : s.ErfNumber, Load = l }).ToListAsync(ct);
        var inputs = rows.Select(x => new LvLoadIn((x.Load?.Id ?? x.Id).ToString(), x.Id.ToString(), x.Erf, [x.Location.X, x.Location.Y],
            x.Load?.Kva, x.Load?.Kind ?? LoadKinds.Residential)).ToList();
        // A residential load point's category is its load class (or the engineer's override); special loads have none.
        var classes = rows.Where(x => x.Load is { Kind: LoadKinds.Residential })
            .ToDictionary(x => x.Load!.Id.ToString(), x => x.Load!.ClassOverride ?? x.Load!.Category);
        return (inputs, classes);
    }
}
