using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Design;
using Reticula.Domain.Field;
using Reticula.Domain.Projects;
using Reticula.Infrastructure.Data;

namespace Reticula.Infrastructure.Design;

/// <summary>What the API reads off a design result to list and gate it. Every value is the calc service's; none is recomputed.</summary>
public sealed record DesignFacts(string RulesHash, string? Construction, bool FitToSubmit, int Checks, int Failures, double? Capex, double? Lifetime,
    IReadOnlyList<string> Placeholders, string SummaryJson);

/// <summary>Stores design runs and reads their facts (ADR 0011).</summary>
public static class DesignRuns
{
    public const string ProjectSubject = "project";

    /// <summary>The project's design: the latest run that produced one design (a run or an adopted optimisation option).</summary>
    public static Task<DesignRun?> CurrentAsync(ReticulaDbContext db, Guid projectId, CancellationToken ct) =>
        db.DesignRuns.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.ResultJson != null && (r.Mode == DesignRunModes.Run || r.Mode == DesignRunModes.Adopted))
            .OrderByDescending(r => r.Number).FirstOrDefaultAsync(ct);

    public static async Task<int> NextNumberAsync(ReticulaDbContext db, Guid projectId, CancellationToken ct) =>
        (await db.DesignRuns.Where(r => r.ProjectId == projectId).MaxAsync(r => (int?)r.Number, ct) ?? 0) + 1;

    public static DesignFacts Design(JsonElement d)
    {
        var summary = d.GetProperty("summary");
        var cost = d.GetProperty("cost");
        var placeholders = d.GetProperty("placeholders").EnumerateArray().Select(p => p.GetString() ?? "").ToList();
        var bulk = d.GetProperty("bulk");
        var facts = JsonNode.Parse(summary.GetRawText())!.AsObject();
        facts["fit_to_submit"] = d.GetProperty("fit_to_submit").GetBoolean();
        facts["placeholders"] = placeholders.Count;
        facts["not_inspected"] = d.GetProperty("not_inspected").GetArrayLength();
        facts["bulk_stopped"] = bulk.TryGetProperty("stopped", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        facts["rate_library"] = cost.TryGetProperty("library", out var lib) ? lib.GetString() : null;
        facts["rate_date"] = cost.TryGetProperty("rate_date", out var rd) ? rd.GetString() : null;
        facts["indicative"] = cost.TryGetProperty("indicative", out var ind) && ind.ValueKind == JsonValueKind.True;
        facts["comparison"] = d.TryGetProperty("comparison", out var cmp) ? JsonNode.Parse(cmp.GetRawText()) : null;
        return new DesignFacts(d.GetProperty("rules_hash").GetString() ?? "", d.GetProperty("construction").GetString(),
            d.GetProperty("fit_to_submit").GetBoolean(), summary.GetProperty("checks").GetInt32(), summary.GetProperty("failures").GetInt32(),
            Num(cost, "capex"), Num(cost, "lifetime"), placeholders, facts.ToJsonString());
    }

    public static DesignFacts Optimisation(JsonElement r)
    {
        var options = r.GetProperty("options").EnumerateArray().ToList();
        var facts = new JsonObject
        {
            ["comparison"] = JsonNode.Parse(r.GetProperty("comparison").GetRawText()),
            ["evaluations"] = r.GetProperty("evaluations").GetInt32(),
            ["siting"] = r.TryGetProperty("siting", out var s) && s.ValueKind == JsonValueKind.Object
                ? new JsonObject { ["status"] = s.GetProperty("status").GetString(), ["optimal"] = s.GetProperty("optimal").GetBoolean() } : null,
        };
        var designs = options.Select(o => Design(o.GetProperty("design"))).ToList();
        var placeholders = designs.SelectMany(d => d.Placeholders).Distinct().ToList();
        facts["placeholders"] = placeholders.Count;
        return new DesignFacts(r.GetProperty("rules_hash").GetString() ?? "", null, designs.Any(d => d.FitToSubmit),
            designs.Sum(d => d.Checks), designs.Sum(d => d.Failures), designs.Min(d => d.Capex), designs.Min(d => d.Lifetime), placeholders,
            facts.ToJsonString());
    }

    /// <summary>Why a design is not fit to submit, read off the facts the calc service reported (empty when it is fit).</summary>
    public static List<string> UnfitReasons(DesignRun run)
    {
        if (run.FitToSubmit) return [];
        var facts = run.SummaryJson is null ? null : JsonNode.Parse(run.SummaryJson)?.AsObject();
        int Count(string name) => facts?[name] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
        var reasons = new List<string>();
        if (run.Failures > 0) reasons.Add($"{run.Failures} failed check{(run.Failures == 1 ? "" : "s")}");
        if (Count("placeholders") is var ph and > 0) reasons.Add($"{ph} placeholder value{(ph == 1 ? "" : "s")} in the rules");
        if (Count("not_inspected") is var ni and > 0) reasons.Add($"{ni} proposed element{(ni == 1 ? "" : "s")} not inspected");
        if (facts?["bulk_stopped"] is JsonValue b && b.TryGetValue<string>(out var stopped)) reasons.Add($"bulk studies not run ({stopped.TrimEnd('.')})");
        if (reasons.Count == 0) reasons.Add("the design reports errors");
        return reasons;
    }

    private static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>
    /// Every rules placeholder the design used is an open assumption on the project (plan 7.1), one per placeholder text. A placeholder
    /// the design no longer uses is cleared; one that comes back is reopened. Confirmed ones stay confirmed.
    /// </summary>
    public static async Task SyncPlaceholdersAsync(ReticulaDbContext db, Guid projectId, int runNumber, IReadOnlyList<string> placeholders, Guid by,
        DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.Assumptions
            .Where(a => a.ProjectId == projectId && a.SubjectType == ProjectSubject && a.Code.StartsWith(AssumptionCodes.RulesPlaceholder))
            .ToDictionaryAsync(a => a.Code, ct);
        var used = new HashSet<string>();
        foreach (var text in placeholders.Distinct())
        {
            var code = $"{AssumptionCodes.RulesPlaceholder}:{CanonicalJson.Sha256(text)[..16]}";
            used.Add(code);
            var message = Trim($"Design run {runNumber} used a placeholder rules value: {text}", 1000);
            if (!existing.TryGetValue(code, out var a))
                db.Assumptions.Add(new Assumption(Guid.CreateVersion7(), projectId, ProjectSubject, projectId, code, message, now));
            else if (a.Status == AssumptionStatus.Cleared) a.Reopen(message, now);
        }
        foreach (var a in existing.Values.Where(a => !used.Contains(a.Code) && a.Status == AssumptionStatus.Open))
            a.Clear(by, $"Design run {runNumber} no longer uses this value.", now);
    }

    /// <summary>
    /// Adopts one option of an optimisation run as the project's design (plan 5.4): a new run whose request is the option's request and
    /// whose result is the option's design, both as the calc service gave them. Nothing is computed again.
    /// </summary>
    public static async Task<DesignRun?> AdoptAsync(ReticulaDbContext db, Project project, DesignRun optimisation, string objective, Guid by,
        DateTimeOffset now, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(optimisation.ResultJson!);
        var option = doc.RootElement.GetProperty("options").EnumerateArray()
            .FirstOrDefault(o => o.GetProperty("objective").GetString() == objective);
        if (option.ValueKind != JsonValueKind.Object) return null;
        var design = option.GetProperty("design");
        var facts = Design(design);
        var run = new DesignRun(Guid.CreateVersion7(), project.Id, await NextNumberAsync(db, project.Id, ct), DesignRunModes.Adopted, optimisation.Id,
            optimisation.RulesRef, optimisation.InputsHash, optimisation.InputsPartsJson, option.GetProperty("request").GetRawText(), by, now, null);
        var raw = design.GetRawText();
        run.Complete(facts.RulesHash, raw, CanonicalJson.Hash(design), facts.Construction, facts.FitToSubmit, facts.Checks, facts.Failures,
            facts.Capex, facts.Lifetime, facts.SummaryJson, now);
        db.DesignRuns.Add(run);
        await SyncPlaceholdersAsync(db, project.Id, run.Number, facts.Placeholders, by, now, ct);
        await db.SaveChangesAsync(ct);
        return run;
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;
}
