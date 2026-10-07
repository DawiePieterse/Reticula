using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Assistant;
using Reticula.Domain.Design;
using Reticula.Domain.Field;
using Reticula.Domain.Layout;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;

namespace Reticula.Infrastructure.Assistant;

/// <summary>What one tool call did: its result for the model, and the draft it proposed, if any.</summary>
public sealed record ToolOutcome(string Content, bool IsError, AssistantDraft? Draft = null);

/// <summary>
/// The assistant's tool gateway (plan 8.1). Each tool reads one project's stored data (never another's: the project is fixed by the
/// conversation, not chosen by the model) or proposes a draft for the engineer. There is no calculation tool: every number the
/// assistant can see is a stored calc service result with its trace. There is no tool that changes authority inputs, rates, rules,
/// assumptions, revisions or sign-off; the only writes are drafts, which take effect only when the engineer accepts them.
/// </summary>
public sealed partial class AssistantTools(ReticulaDbContext db, TimeProvider time)
{
    private const int MaxContent = 20_000;

    private static readonly string[] Categories =
    [
        "lv_drop", "lv_loading", "lv_fault", "oh_clearance", "oh_tension", "oh_pole", "tx_loading", "mv_loading", "mv_drop", "bulk_supply",
        "bulk_fault", "bulk_withstand", "bulk_voltage", "not_inspected",
    ];

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>The tools as the Messages API takes them; every schema refuses properties it does not name.</summary>
    public static JsonArray Definitions() =>
    [
        Tool("get_project", "The project: name, rules version, buildings and loads, marked routes and sites, connection point status, design runs.",
            """{"type":"object","properties":{},"additionalProperties":false}"""),
        Tool("get_design", "The project's current design: summary, fitness to submit and why not, failed checks, placeholders, uninspected elements, cost.",
            """{"type":"object","properties":{},"additionalProperties":false}"""),
        Tool("get_checks", "Checks of the current design with value, limit, unit, clause and formula id.",
            $$$"""{"type":"object","properties":{"category":{"type":"string","enum":{{{JsonSerializer.Serialize(Categories)}}}},"failing_only":{"type":"boolean"},"limit":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}"""),
        Tool("get_traces", "Traceability records of the current design: value, unit, formula id and text, clause, rules hash and named inputs. Explain any number only from these.",
            """{"type":"object","properties":{"formula_id":{"type":"string","maxLength":100},"path":{"type":"string","maxLength":200},"limit":{"type":"integer","minimum":1,"maximum":30}},"additionalProperties":false}"""),
        Tool("get_option_comparison", "The latest optimisation run: its options side by side, the moves that made each and which are too close to call.",
            """{"type":"object","properties":{},"additionalProperties":false}"""),
        Tool("list_assumptions", "The project's assumptions register.",
            """{"type":"object","properties":{"status":{"type":"string","enum":["open","confirmed","cleared"]}},"additionalProperties":false}"""),
        Tool("propose_run_parameters", $"Propose design run parameters for the engineer to accept or reject. Nothing runs until the engineer accepts. Design options: {string.Join(", ", RunOptions.DesignKeys)}. Optimisation options: {string.Join(", ", RunOptions.OptimiseKeys)}.",
            """{"type":"object","properties":{"mode":{"type":"string","enum":["run","optimise"]},"options":{"type":"object","additionalProperties":true},"optimise":{"type":"object","additionalProperties":true},"explanation":{"type":"string","maxLength":2000}},"required":["mode","explanation"],"additionalProperties":false}"""),
        Tool("draft_report_section", "Draft a text section of the design report for the engineer to accept, edit and approve. State only what the project's records and traces say.",
            """{"type":"object","properties":{"key":{"type":"string","maxLength":50},"title":{"type":"string","maxLength":200},"text":{"type":"string","maxLength":20000},"explanation":{"type":"string","maxLength":1000}},"required":["key","title","text"],"additionalProperties":false}"""),
    ];

    private static JsonObject Tool(string name, string description, string schema) =>
        new() { ["name"] = name, ["description"] = description, ["input_schema"] = Obj(schema) };

    private static readonly Dictionary<string, JsonObject> Schemas =
        Definitions().OfType<JsonObject>().ToDictionary(t => t["name"]!.GetValue<string>(), t => t["input_schema"]!.AsObject());

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,49}$")]
    private static partial Regex SectionKey();

    public async Task<ToolOutcome> RunAsync(Guid projectId, Guid conversationId, string name, JsonNode? input, CancellationToken ct)
    {
        if (!Schemas.TryGetValue(name, out var schema)) return Error($"There is no tool '{name}'. The tools are: {string.Join(", ", Schemas.Keys)}.");
        var errors = SchemaCheck.Errors(schema, input ?? new JsonObject());
        if (errors.Count > 0) return Error(string.Join(" ", errors));
        var args = (input ?? new JsonObject()).AsObject();
        return name switch
        {
            "get_project" => Ok(await ProjectAsync(projectId, ct)),
            "get_design" => await WithDesignAsync(projectId, d => Ok(Design(d)), ct),
            "get_checks" => await WithDesignAsync(projectId, d => Ok(Checks(d, args)), ct),
            "get_traces" => await WithDesignAsync(projectId, d => Ok(Traces(d, args)), ct),
            "get_option_comparison" => await ComparisonAsync(projectId, ct),
            "list_assumptions" => Ok(await AssumptionsAsync(projectId, args["status"]?.GetValue<string>(), ct)),
            "propose_run_parameters" => ProposeRun(projectId, conversationId, args),
            "draft_report_section" => DraftSection(projectId, conversationId, args),
            _ => Error($"There is no tool '{name}'."),
        };
    }

    private static ToolOutcome Ok(JsonNode result)
    {
        var text = result.ToJsonString();
        return new ToolOutcome(text.Length <= MaxContent ? text : text[..MaxContent] + " …(cut short: ask for less with a filter or limit)", false);
    }

    private static ToolOutcome Error(string message) => new(message, true);

    private async Task<JsonObject> ProjectAsync(Guid projectId, CancellationToken ct)
    {
        var p = await db.Projects.ActiveAsync(projectId, ct);
        var cp = await db.ConnectionPoints.AsNoTracking().FirstOrDefaultAsync(c => c.ProjectId == projectId, ct);
        var kinds = await db.Candidates.AsNoTracking().Where(c => c.ProjectId == projectId && c.ArchivedAt == null)
            .GroupBy(c => new { c.Kind, c.Source }).Select(g => new { g.Key.Kind, g.Key.Source, Count = g.Count() }).ToListAsync(ct);
        var runs = await db.DesignRuns.AsNoTracking().Where(r => r.ProjectId == projectId).OrderByDescending(r => r.Number).Take(10)
            .Select(r => new { r.Number, r.Mode, r.FitToSubmit, r.Failures, r.Capex, r.CreatedAt }).ToListAsync(ct);
        return new JsonObject
        {
            ["name"] = p?.Name, ["rules"] = p?.RulesRef,
            ["buildings"] = await db.Buildings.CountAsync(b => b.ProjectId == projectId && b.Status != BuildingStatus.NotPresent, ct),
            ["loads"] = await db.LoadPoints.CountAsync(l => l.ProjectId == projectId, ct),
            ["candidates"] = JsonSerializer.SerializeToNode(kinds),
            ["connection_point"] = cp is null ? "not entered" : cp.IsComplete ? "capacity and fault level entered" : "located, capacity or fault level missing",
            ["design_runs"] = JsonSerializer.SerializeToNode(runs),
            ["open_assumptions"] = await db.Assumptions.CountAsync(a => a.ProjectId == projectId && a.Status == AssumptionStatus.Open, ct),
        };
    }

    private async Task<ToolOutcome> WithDesignAsync(Guid projectId, Func<JsonElement, ToolOutcome> f, CancellationToken ct)
    {
        var run = await DesignRuns.CurrentAsync(db, projectId, ct);
        if (run?.ResultJson is null) return Error("The project has no design yet: the engineer has to run one.");
        using var doc = JsonDocument.Parse(run.ResultJson);
        var outcome = f(doc.RootElement);
        return outcome with { Content = $"{{\"design_run\":{run.Number},\"result\":{outcome.Content}}}" };
    }

    private static JsonObject Design(JsonElement d)
    {
        var failed = d.GetProperty("checks").EnumerateArray().Where(c => !c.GetProperty("passes").GetBoolean()).Take(50).ToList();
        return new JsonObject
        {
            ["rules"] = $"{d.GetProperty("rules_ref").GetString()} ({d.GetProperty("rules_hash").GetString()})",
            ["construction"] = d.GetProperty("construction").GetString(),
            ["fit_to_submit"] = d.GetProperty("fit_to_submit").GetBoolean(),
            ["summary"] = JsonNode.Parse(d.GetProperty("summary").GetRawText()),
            ["failed_checks"] = new JsonArray([.. failed.Select(c => JsonNode.Parse(c.GetRawText()))]),
            ["placeholders"] = JsonNode.Parse(d.GetProperty("placeholders").GetRawText()),
            ["not_inspected"] = d.GetProperty("not_inspected").GetArrayLength(),
            ["bulk_stopped"] = d.GetProperty("bulk").TryGetProperty("stopped", out var s) ? JsonNode.Parse(s.GetRawText()) : null,
            ["cost"] = new JsonObject
            {
                ["capex"] = Num(d.GetProperty("cost"), "capex"), ["lifetime"] = Num(d.GetProperty("cost"), "lifetime"),
                ["library"] = d.GetProperty("cost").TryGetProperty("library", out var l) ? l.GetString() : null,
                ["indicative"] = d.GetProperty("cost").TryGetProperty("indicative", out var i) && i.ValueKind == JsonValueKind.True,
            },
        };
    }

    private static JsonArray Checks(JsonElement d, JsonObject args)
    {
        var category = args["category"]?.GetValue<string>();
        var failing = args["failing_only"]?.GetValue<bool>() == true;
        var limit = args["limit"]?.GetValue<int>() ?? 50;
        return [.. d.GetProperty("checks").EnumerateArray()
            .Where(c => (category is null || c.GetProperty("category").GetString() == category) && (!failing || !c.GetProperty("passes").GetBoolean()))
            .Take(limit).Select(c => JsonNode.Parse(c.GetRawText()))];
    }

    private static JsonArray Traces(JsonElement d, JsonObject args)
    {
        var formula = args["formula_id"]?.GetValue<string>();
        var prefix = args["path"]?.GetValue<string>();
        var limit = args["limit"]?.GetValue<int>() ?? 20;
        var found = new JsonArray();
        void Walk(JsonElement e, string path)
        {
            if (found.Count >= limit) return;
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("formula_id", out var f) && e.TryGetProperty("value", out _) && e.TryGetProperty("unit", out _))
                {
                    if ((formula is null || f.GetString() == formula) && (prefix is null || path.StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        var t = JsonNode.Parse(e.GetRawText())!.AsObject();
                        t["path"] = path;
                        found.Add(t);
                    }
                    return;
                }
                foreach (var p in e.EnumerateObject()) Walk(p.Value, path.Length == 0 ? p.Name : $"{path}.{p.Name}");
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var x in e.EnumerateArray()) Walk(x, $"{path}[{i++}]");
            }
        }
        Walk(d, "");
        return found;
    }

    private async Task<ToolOutcome> ComparisonAsync(Guid projectId, CancellationToken ct)
    {
        var run = await db.DesignRuns.AsNoTracking().Where(r => r.ProjectId == projectId && r.Mode == DesignRunModes.Optimise && r.ResultJson != null)
            .OrderByDescending(r => r.Number).FirstOrDefaultAsync(ct);
        if (run is null) return Error("The project has no optimisation run yet.");
        using var doc = JsonDocument.Parse(run.ResultJson!);
        var r = doc.RootElement;
        return Ok(new JsonObject
        {
            ["design_run"] = run.Number,
            ["comparison"] = JsonNode.Parse(r.GetProperty("comparison").GetRawText()),
            ["moves"] = new JsonObject([.. r.GetProperty("options").EnumerateArray().Select(o =>
                KeyValuePair.Create(o.GetProperty("objective").GetString()!, JsonNode.Parse(o.GetProperty("moves").GetRawText())))]),
        });
    }

    private async Task<JsonArray> AssumptionsAsync(Guid projectId, string? status, CancellationToken ct)
    {
        var q = db.Assumptions.AsNoTracking().Where(a => a.ProjectId == projectId);
        if (Enum.TryParse<AssumptionStatus>(status, true, out var s)) q = q.Where(a => a.Status == s);
        var rows = await q.OrderBy(a => a.CreatedAt).Take(100).Select(a => new { a.Code, a.Text, a.Status, a.ClearNote }).ToListAsync(ct);
        return [.. rows.Select(a => (JsonNode)new JsonObject
        {
            ["code"] = a.Code, ["text"] = a.Text, ["status"] = a.Status.ToString().ToLowerInvariant(), ["note"] = a.ClearNote,
        })];
    }

    private ToolOutcome ProposeRun(Guid projectId, Guid conversationId, JsonObject args)
    {
        var mode = args["mode"]!.GetValue<string>();
        var options = args["options"] is JsonObject o ? JsonDocument.Parse(o.ToJsonString()).RootElement : (JsonElement?)null;
        var optimise = args["optimise"] is JsonObject x ? JsonDocument.Parse(x.ToJsonString()).RootElement : (JsonElement?)null;
        var errors = RunOptions.ValidateDesign(options);
        if (mode == DesignRunModes.Optimise) foreach (var e in RunOptions.ValidateOptimise(optimise)) errors[e.Key] = e.Value;
        else if (optimise is not null) errors["optimise"] = ["Only for mode optimise."];
        if (errors.Count > 0) return Error("Refused: " + string.Join(" ", errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}")));
        var payload = new JsonObject { ["mode"] = mode, ["options"] = args["options"]?.DeepClone(), ["optimise"] = args["optimise"]?.DeepClone() };
        var draft = new AssistantDraft(Guid.CreateVersion7(), projectId, conversationId, AssistantDraftKinds.RunParameters, payload.ToJsonString(),
            args["explanation"]!.GetValue<string>(), time.GetUtcNow());
        db.AssistantDrafts.Add(draft);
        return new ToolOutcome($"{{\"draft\":\"{draft.Id}\",\"status\":\"proposed\",\"note\":\"The engineer will accept or reject it; nothing has run.\"}}", false, draft);
    }

    private ToolOutcome DraftSection(Guid projectId, Guid conversationId, JsonObject args)
    {
        var key = args["key"]!.GetValue<string>();
        if (!SectionKey().IsMatch(key)) return Error("Refused: key must be lower-case letters, digits and hyphens.");
        var payload = new JsonObject { ["key"] = key, ["title"] = args["title"]!.GetValue<string>(), ["text"] = args["text"]!.GetValue<string>() };
        var draft = new AssistantDraft(Guid.CreateVersion7(), projectId, conversationId, AssistantDraftKinds.ReportSection, payload.ToJsonString(),
            args["explanation"]?.GetValue<string>() ?? "", time.GetUtcNow());
        db.AssistantDrafts.Add(draft);
        return new ToolOutcome($"{{\"draft\":\"{draft.Id}\",\"status\":\"proposed\",\"note\":\"The engineer will accept, edit and approve it.\"}}", false, draft);
    }

    private static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
