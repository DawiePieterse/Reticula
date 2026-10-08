using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Field;
using Reticula.Domain.Layout;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;

namespace Reticula.Infrastructure.Field;

public sealed record LoadScheduleRow(
    string? Erf, Guid BuildingId, string BuildingType, string BuildingStatus, string? LoadKind, string? Category, string? IncomeBand,
    double? Kva, double? EstimatedKva, bool Overridden, string? OverrideReason, string? LoadStatus);

public sealed record LoadScheduleTotals(int ResidentialCount, int SpecialCount, double? DiversityFactor, double ResidentialKva,
    double SpecialKva, double TotalKva, string Formula, string Clause, string Method, int? Phases, double? ConfidencePct, double? DesignCurrentA);

public sealed record LoadSchedule(string Project, string RulesRef, string RulesHash, DateTimeOffset GeneratedAt,
    IReadOnlyList<LoadScheduleRow> Rows, LoadScheduleTotals? Totals);

/// <summary>The load schedule (plan 1.6, document 6.3): every standing building with its load, and the diversified totals from the calc service.</summary>
public static class LoadSchedules
{
    public static async Task<LoadSchedule?> BuildAsync(ReticulaDbContext db, ICalcClient calc, TimeProvider time, Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.ActiveAsync(projectId, ct);
        if (project is null) return null;

        var rows = await (
            from b in db.Buildings.AsNoTracking()
            where b.ProjectId == projectId && b.Status != BuildingStatus.NotPresent
            join s in db.Stands.AsNoTracking() on b.StandId equals s.Id into ss
            from s in ss.DefaultIfEmpty()
            join l in db.LoadPoints.AsNoTracking() on b.Id equals l.BuildingId into ls
            from l in ls.DefaultIfEmpty()
            orderby s.ErfNumber, b.Id
            select new { b, Erf = s == null ? null : s.ErfNumber, l }).ToListAsync(ct);

        var scheduleRows = rows.Select(x => new LoadScheduleRow(
            x.Erf, x.b.Id, x.b.ConfirmedType ?? x.b.PredictedType, x.b.Status.ToString().ToLowerInvariant(),
            x.l?.Kind, x.l?.Category, x.l?.IncomeBand, x.l?.Kva, x.l?.EstimatedKva, x.l?.Overridden ?? false, x.l?.OverrideReason,
            x.l?.Status.ToString().ToLowerInvariant())).ToList();

        var loads = rows.Where(x => x.l is not null)
            .Select(x => new AdmdGroupLoad(x.l!.Id.ToString(), x.l.Kind, x.l.Kva, x.l.Kind == LoadKinds.Residential ? x.l.Category : null)).ToList();
        LoadScheduleTotals? totals = null;
        var rulesHash = rows.FirstOrDefault(x => x.l is not null)?.l!.RulesHash ?? "";
        if (loads.Count > 0)
        {
            var g = await calc.GroupAdmdAsync(project.RulesRef, loads, ct);
            rulesHash = g.RulesHash;
            totals = new LoadScheduleTotals(g.ResidentialCount, g.SpecialCount, g.DiversityFactor?.Value, g.ResidentialKva.Value,
                g.SpecialKva, g.TotalKva.Value, g.TotalKva.Formula, g.TotalKva.Clause, g.Method, g.Phases, g.ConfidencePct, g.DesignCurrentA?.Value);
        }
        return new LoadSchedule(project.Name, project.RulesRef, rulesHash, time.GetUtcNow(), scheduleRows, totals);
    }
}
