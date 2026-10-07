using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Design;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Infrastructure.Design;

/// <param name="Mode">run, optimise or reproduce.</param>
/// <param name="Options">Design options (calc design.DesignOptions, snake_case).</param>
/// <param name="Optimise">Optimisation options (calc optimise.OptimiseOptions).</param>
/// <param name="ParentRunId">For a reproduction: the run whose stored request is sent again.</param>
/// <param name="RevisionId">For a reproduction: the revision it checks.</param>
public sealed record DesignRunPayload(string Mode, JsonElement? Options = null, JsonElement? Optimise = null, Guid? ParentRunId = null, Guid? RevisionId = null);

/// <summary>
/// A design run (ADR 0011): gathers the project's inputs, sends them with the engineer's options to the calc service and stores the
/// request and the result exactly. A reproduction (plan 7.2) sends a stored request again and compares the results bit for bit.
/// </summary>
public sealed class DesignRunJob(ReticulaDbContext db, ICalcClient calc, DesignInputsBuilder inputs, TimeProvider time) : IJobHandler
{
    public const string JobKind = "design.run";
    public string Kind => JobKind;

    public async Task<object?> RunAsync(JobContext context, CancellationToken ct)
    {
        var project = (context.ProjectId is { } projectId ? await db.Projects.ActiveAsync(projectId, ct) : null)
                      ?? throw new InvalidOperationException("The project no longer exists.");
        var payload = context.Payload.Deserialize<DesignRunPayload>(JobJson) ?? throw new InvalidOperationException("The job has no payload.");
        if (payload.Mode == DesignRunModes.Reproduce) return await ReproduceAsync(context, project.Id, payload, ct);

        await context.Progress.ReportAsync(5, "Gathering routes, sites, loads and rates", ct);
        var input = await inputs.BuildAsync(project, context.RequestedBy, ct);
        var request = input.Request(payload.Options);
        var optimise = payload.Mode == DesignRunModes.Optimise;
        if (optimise)
            request = $"{{\"design\":{request},\"options\":{(payload.Optimise is { ValueKind: JsonValueKind.Object } o ? o.GetRawText() : "{}")}}}";

        await context.Progress.ReportAsync(15, optimise
            ? $"Optimising: {input.Loads} loads, {input.Candidates} routes and sites; each option is a full design"
            : $"Designing {input.Loads} loads on {input.Candidates} routes and sites", ct);
        var raw = optimise ? await calc.OptimiseDesignAsync(request, ct) : await calc.RunDesignAsync(request, ct);

        await context.Progress.ReportAsync(90, "Saving the design", ct);
        using var doc = JsonDocument.Parse(raw);
        var facts = optimise ? DesignRuns.Optimisation(doc.RootElement) : DesignRuns.Design(doc.RootElement);
        var now = time.GetUtcNow();
        var run = new DesignRun(Guid.CreateVersion7(), project.Id, await DesignRuns.NextNumberAsync(db, project.Id, ct), payload.Mode, null,
            project.RulesRef, input.Hash, JsonSerializer.Serialize(input.Parts), request, context.RequestedBy, now, context.JobId);
        run.Complete(facts.RulesHash, raw, CanonicalJson.Hash(doc.RootElement), facts.Construction, facts.FitToSubmit, facts.Checks, facts.Failures,
            facts.Capex, facts.Lifetime, facts.SummaryJson, now);
        db.DesignRuns.Add(run);
        if (!optimise) await DesignRuns.SyncPlaceholdersAsync(db, project.Id, run.Number, facts.Placeholders, context.RequestedBy, now, ct);
        await db.SaveChangesAsync(ct);
        return new
        {
            runId = run.Id, number = run.Number, mode = run.Mode, fitToSubmit = run.FitToSubmit, checks = run.Checks, failures = run.Failures,
            capex = run.Capex, placeholders = facts.Placeholders.Count, loadsWithoutEstimate = input.LoadsWithoutEstimate,
            connectionPoint = input.ConnectionPointComplete,
        };
    }

    private async Task<object> ReproduceAsync(JobContext context, Guid projectId, DesignRunPayload payload, CancellationToken ct)
    {
        var original = await db.DesignRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == payload.ParentRunId && r.ProjectId == projectId, ct)
                       ?? throw new InvalidOperationException("The design run to reproduce no longer exists.");
        if (original.ResultJson is null) throw new InvalidOperationException($"Design run {original.Number} has no result to compare with.");

        await context.Progress.ReportAsync(10, $"Running design run {original.Number}'s stored request again", ct);
        var optimise = original.Mode == DesignRunModes.Optimise;
        var raw = optimise ? await calc.OptimiseDesignAsync(original.RequestJson, ct) : await calc.RunDesignAsync(original.RequestJson, ct);

        await context.Progress.ReportAsync(90, "Comparing the results", ct);
        using var again = JsonDocument.Parse(raw);
        using var before = JsonDocument.Parse(original.ResultJson);
        var hash = CanonicalJson.Hash(again.RootElement);
        var identical = hash == original.ResultHash;
        var differences = identical ? [] : CanonicalJson.Differences(before.RootElement, again.RootElement);
        var facts = optimise ? DesignRuns.Optimisation(again.RootElement) : DesignRuns.Design(again.RootElement);
        var now = time.GetUtcNow();
        var run = new DesignRun(Guid.CreateVersion7(), projectId, await DesignRuns.NextNumberAsync(db, projectId, ct), DesignRunModes.Reproduce, original.Id,
            original.RulesRef, original.InputsHash, original.InputsPartsJson, original.RequestJson, context.RequestedBy, now, context.JobId);
        run.Complete(facts.RulesHash, raw, hash, facts.Construction, facts.FitToSubmit, facts.Checks, facts.Failures, facts.Capex, facts.Lifetime,
            facts.SummaryJson, now);
        db.DesignRuns.Add(run);
        if (payload.RevisionId is { } revisionId && await db.Revisions.FirstOrDefaultAsync(r => r.Id == revisionId && r.ProjectId == projectId, ct) is { } rev)
            rev.RecordReproduction(identical, now);
        await db.SaveChangesAsync(ct);
        return new
        {
            runId = run.Id, number = run.Number, reproduces = original.Number, identical, resultHash = hash, originalHash = original.ResultHash,
            rulesHashChanged = facts.RulesHash != original.RulesHash, differences,
        };
    }

    private static readonly JsonSerializerOptions JobJson = new(JsonSerializerDefaults.Web);
}
