using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Design;
using Reticula.Domain.Documents;
using Reticula.Domain.Field;
using Reticula.Domain.Projects;
using Reticula.Domain.Review;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;

namespace Reticula.Infrastructure.Review;

/// <summary>Revisions (plan 7.2) and sign-off (plan 7.3): what a revision is made from and what must hold before the engineer signs it.</summary>
public sealed class ReviewService(ReticulaDbContext db, DesignInputsBuilder inputs, TimeProvider time)
{
    public const string DefaultStatement =
        "I have reviewed this design and the calculations it rests on, and I take professional responsibility for it.";

    /// <summary>A revision of a design run; the project's current design when no run is named.</summary>
    /// <returns>Null with a reason when the run cannot be issued.</returns>
    public async Task<(Revision? Revision, string? Problem)> CreateAsync(Project project, Guid? designRunId, string? label, string? description, Guid by,
        CancellationToken ct)
    {
        var run = designRunId is { } id
            ? await db.DesignRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.ProjectId == project.Id, ct)
            : await DesignRuns.CurrentAsync(db, project.Id, ct);
        if (run is null) return (null, "There is no design run to issue.");
        if (!run.HasDesign || run.Mode == DesignRunModes.Reproduce)
            return (null, "Only a design run or an adopted optimisation option can be issued as a revision.");
        var number = (await db.Revisions.Where(r => r.ProjectId == project.Id).MaxAsync(r => (int?)r.Number, ct) ?? 0) + 1;
        var rev = new Revision(Guid.CreateVersion7(), project.Id, number, string.IsNullOrWhiteSpace(label) ? $"Rev {number}" : label.Trim(),
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(), run.Id, run.RulesRef, run.RulesHash, run.InputsHash, run.ResultHash!,
            run.FitToSubmit, by, time.GetUtcNow());
        db.Revisions.Add(rev);
        await db.SaveChangesAsync(ct);
        return (rev, null);
    }

    /// <summary>
    /// Everything that stops the engineer signing this revision off; empty when it can be signed. The design must be fit to submit (no
    /// failed check, no placeholder, nothing uninspected, the bulk studies run), no assumption may be open, the project's inputs must be
    /// the ones the revision was designed from, the stored request must have reproduced the result, and its documents must be generated.
    /// </summary>
    public async Task<List<string>> SignOffBlockersAsync(Project project, Revision rev, string? registrationNo, CancellationToken ct)
    {
        var reasons = new List<string>();
        if (rev.Locked) reasons.Add($"Revision {rev.Number} is already signed off.");
        if (string.IsNullOrWhiteSpace(registrationNo)) reasons.Add("Your account has no ECSA registration number; an engineer must add it under Users before you can sign off.");
        var run = await db.DesignRuns.AsNoTracking().FirstAsync(r => r.Id == rev.DesignRunId, ct);
        if (!run.FitToSubmit) reasons.Add($"Design run {run.Number} is not fit to submit: {string.Join("; ", DesignRuns.UnfitReasons(run))}.");
        var open = await db.Assumptions.CountAsync(a => a.ProjectId == project.Id && a.Status == AssumptionStatus.Open, ct);
        if (open > 0) reasons.Add($"{open} assumption(s) are still open: clear or confirm each one.");
        var now = await inputs.BuildAsync(project, rev.CreatedBy, ct);
        if (now.Hash != rev.InputsHash) reasons.Add(now.StaleReason(run.InputsPartsJson) ?? "The project's inputs changed after this revision was made.");
        if (rev.Reproduced != true)
            reasons.Add(rev.Reproduced is null ? "Reproduce the revision first, to show its stored request gives the same result." : "The last reproduction gave a different result.");
        var kinds = await db.Documents.Where(d => d.RevisionId == rev.Id && d.SupersededAt == null).Select(d => d.Kind).Distinct().ToListAsync(ct);
        var missing = DocumentKinds.All.Except(kinds).ToList();
        if (missing.Count > 0) reasons.Add($"Generate the documents for revision {rev.Number} first ({missing.Count} missing).");
        return reasons;
    }
}
