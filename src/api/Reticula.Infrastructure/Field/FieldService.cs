using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Field;
using Reticula.Domain.Layout;
using Reticula.Domain.Projects;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;

namespace Reticula.Infrastructure.Field;

/// <summary>Load estimates and the assumptions they create. The calc service does every number.</summary>
public sealed class FieldService(ReticulaDbContext db, ICalcClient calc, TimeProvider time)
{
    public const string LoadPointSubject = "load_point";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Estimates (or re-estimates) the building's load and keeps its assumptions in step. The save is recorded as an
    /// inspection with id <paramref name="opId"/> (generated on the device) so a change synced twice is applied once.
    /// </summary>
    /// <exception cref="CalcRejectedException">Observations the rules file does not accept.</exception>
    public async Task<LoadPoint> EstimateLoadAsync(Project project, Building building, AdmdEstimateRequest request, string? erf,
        uint? expectedVersion, Guid? opId, DateTimeOffset? capturedAt, Guid userId, CancellationToken ct)
    {
        var estimate = await calc.EstimateAdmdAsync(request with { Rules = project.RulesRef }, ct);

        var lp = await db.LoadPoints.FirstOrDefaultAsync(l => l.BuildingId == building.Id, ct);
        if (lp is null)
        {
            lp = new LoadPoint(Guid.CreateVersion7(), project.Id, building.Id);
            db.LoadPoints.Add(lp);
        }
        else if (expectedVersion is not null)
        {
            db.Entry(lp).Property(l => l.Version).OriginalValue = expectedVersion.Value;
        }

        var now = time.GetUtcNow();
        lp.SetEstimate(estimate.Kind, request.SpecialLoad, JsonSerializer.Serialize(request.Observations ?? [], Json),
            estimate.LoadClass?.ChosenBy == "engineer" ? estimate.LoadClass.Code : null, estimate.IncomeBand, estimate.Category, estimate.EstimatedKva, estimate.AdmdKva.Value, estimate.Overridden,
            estimate.Overridden ? request.OverrideReason : null, JsonSerializer.Serialize(estimate.Missing, Json),
            estimate.Raw, estimate.RulesHash, userId, now);

        var assumptions = await db.Assumptions
            .Where(a => a.ProjectId == project.Id && a.SubjectType == LoadPointSubject && a.SubjectId == lp.Id)
            .ToListAsync(ct);
        var byCode = assumptions.GroupBy(a => a.Code).ToDictionary(g => g.Key, g => g.First());
        var label = erf is null ? "building without an erf" : $"erf {erf}";
        var kva = Kva(estimate.AdmdKva.Value);
        var est = Kva(estimate.EstimatedKva);
        Upsert(byCode, project.Id, lp.Id, AssumptionCodes.AdmdEstimated, true,
            estimate.Kind == LoadKinds.Special
                ? $"Special load '{request.SpecialLoad}' at {label} taken as {kva} kVA (rules default {est} kVA)."
                : estimate.LoadClass is { } lc
                    ? $"ADMD at {label} taken as {est} kVA: class {lc.Description} ({lc.TableSource}), " +
                      (lc.ChosenBy == "engineer" ? "chosen by the engineer." : $"from the site-observation score (band {estimate.IncomeBand}).")
                    : $"ADMD at {label} estimated as {est} kVA from site observations (income band {estimate.IncomeBand}, category {estimate.Category}).",
            userId, now);
        Upsert(byCode, project.Id, lp.Id, AssumptionCodes.AdmdOverridden, estimate.Overridden,
            $"ADMD at {label} overridden to {kva} kVA (method gave {est} kVA): {request.OverrideReason}", userId, now);
        Upsert(byCode, project.Id, lp.Id, AssumptionCodes.IndicatorsMissing, estimate.Missing.Count > 0,
            $"At {label}, {estimate.Missing.Count} indicator(s) not recorded and scored as zero: {string.Join(", ", estimate.Missing)}.",
            userId, now);

        db.Inspections.Add(new Inspection(opId ?? Guid.CreateVersion7(), project.Id, InspectionActions.Load, building.Id, null, estimate.Kind,
            null, null, capturedAt ?? now, null, userId, now));
        await db.SaveChangesAsync(ct);
        return lp;
    }

    /// <summary>Engineer accepts the load point; its open assumptions are cleared.</summary>
    public async Task ConfirmLoadAsync(LoadPoint lp, Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        lp.Confirm(userId, now);
        var open = await db.Assumptions
            .Where(a => a.SubjectType == LoadPointSubject && a.SubjectId == lp.Id && a.Status == AssumptionStatus.Open)
            .ToListAsync(ct);
        foreach (var a in open) a.Clear(userId, "Load point confirmed by the engineer.", now);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Opens, reopens or clears the load point's assumption of this code. <paramref name="existing"/> is the point's assumptions by code.</summary>
    private void Upsert(Dictionary<string, Assumption> existing, Guid projectId, Guid subjectId, string code, bool applies, string text, Guid userId, DateTimeOffset now)
    {
        var current = existing.GetValueOrDefault(code);
        if (applies)
        {
            if (current is null) db.Assumptions.Add(new Assumption(Guid.CreateVersion7(), projectId, LoadPointSubject, subjectId, code, text, now));
            else current.Reopen(text, now);
        }
        else if (current is { Status: AssumptionStatus.Open })
        {
            current.Clear(userId, "No longer applies after re-estimate.", now);
        }
    }

    private static string Kva(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
