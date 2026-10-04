using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Auth;
using Reticula.Domain.Costs;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Costs;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;

namespace Reticula.Api.Costs;

public sealed record RateListSummary(Guid Id, string Name, string BasedOn, DateOnly RateDate, string Currency, int Revision, int Overrides, DateTimeOffset UpdatedAt, uint Version);

public sealed record RateListDetail(RateListSummary List, IReadOnlyList<RateRow> Rows, JsonElement Overrides, JsonElement Assemblies);

public sealed record RateListsIndex(IReadOnlyList<string> Shipped, IReadOnlyList<RateListSummary> Lists);

public sealed record CreateRateListRequest(string Name, string BasedOn, DateOnly? RateDate);

public sealed record RateChange(string Section, string? Code, double Rate, DateOnly? Date, string? Source, string? Description, string? Unit);

public sealed record ChangeRatesRequest(IReadOnlyList<RateChange> Changes, DateOnly? RateDate, uint Version);

public sealed record ImportResult(RateListDetail List, int Applied, IReadOnlyList<string> Unknown, IReadOnlyList<string> Errors);

public sealed record ProjectRateList(Guid? RateListId, string Name);

public sealed record SetProjectRateList(Guid? RateListId);

/// <summary>Rate lists and the material library (plan 6.1): copy a shipped list, override rates with a date, import a supplier price list.</summary>
public static class RateListEndpoints
{
    public static IEndpointRouteBuilder MapRateListEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/rate-lists").WithTags("Costs").RequireAuthorization(Policies.FieldUser);
        g.MapGet("/", Index);
        g.MapPost("/", Create).RequireAuthorization(Policies.Engineer);
        g.MapGet("/{id:guid}", Get);
        g.MapPut("/{id:guid}/rates", Change).RequireAuthorization(Policies.Engineer);
        g.MapPost("/{id:guid}/import", Import).RequireAuthorization(Policies.Engineer).DisableAntiforgery();

        var p = app.MapGroup("/api/projects/{projectId:guid}/rate-list").WithTags("Costs").RequireAuthorization(Policies.FieldUser);
        p.MapGet("/", GetProjectList);
        p.MapPut("/", SetProjectList).RequireAuthorization(Policies.Engineer);
        return app;
    }

    private static RateListSummary Summary(RateList r) =>
        new(r.Id, r.Name, r.BasedOn, r.RateDate, r.Currency, r.Revision, JsonNode.Parse(r.OverridesJson)!.AsArray().Count, r.UpdatedAt, r.Version);

    private static RateListDetail Detail(RateList r)
    {
        var content = JsonNode.Parse(r.ContentJson)!.AsObject();
        var overrides = JsonNode.Parse(r.OverridesJson)!.AsArray();
        return new RateListDetail(Summary(r), RateListContent.Rows(content, overrides), JsonSerializer.SerializeToElement(overrides),
            JsonSerializer.SerializeToElement(content["assemblies"] ?? new JsonObject()));
    }

    private static async Task<Ok<RateListsIndex>> Index(ReticulaDbContext db, ICalcClient calc, CancellationToken ct)
    {
        var lists = await db.RateLists.AsNoTracking().Where(r => r.ArchivedAt == null).OrderByDescending(r => r.UpdatedAt).ToListAsync(ct);
        return TypedResults.Ok(new RateListsIndex(await calc.ListRatesAsync(ct), [.. lists.Select(Summary)]));
    }

    private static async Task<Results<Created<RateListDetail>, ValidationProblem>> Create(CreateRateListRequest req, ReticulaDbContext db, ICalcClient calc,
        TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 100) errors["name"] = ["Give the rate list a name of up to 100 characters."];
        JsonObject? content = null;
        var basedOn = req.BasedOn?.Trim() ?? "";
        if (Guid.TryParse(basedOn, out var parentId))
        {
            var parent = await db.RateLists.AsNoTracking().FirstOrDefaultAsync(r => r.Id == parentId, ct);
            if (parent is not null)
            {
                content = JsonNode.Parse(parent.ContentJson)!.AsObject();
                basedOn = $"{parent.Name} (rev {parent.Revision})";
            }
        }
        else if (basedOn.Length > 0 && await calc.GetRatesAsync(basedOn, ct) is { } shipped)
        {
            content = JsonNode.Parse(shipped.GetRawText())!.AsObject();
        }
        if (content is null) errors["basedOn"] = ["Copy a shipped rate list or an existing rate list."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var now = time.GetUtcNow();
        var rateDate = req.RateDate ?? (DateOnly.TryParse(content!["rate_date"]?.ToString(), CultureInfo.InvariantCulture, out var d) ? d : DateOnly.FromDateTime(now.UtcDateTime));
        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        content!.Remove("overrides");
        var list = new RateList(Guid.CreateVersion7(), req.Name.Trim(), basedOn, rateDate, content["currency"]?.GetValue<string>() ?? "ZAR", content.ToJsonString(), userId, now);
        db.RateLists.Add(list);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/rate-lists/{list.Id}", Detail(list));
    }

    private static async Task<Results<Ok<RateListDetail>, NotFound>> Get(Guid id, ReticulaDbContext db, CancellationToken ct)
    {
        var list = await db.RateLists.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        return list is null ? TypedResults.NotFound() : TypedResults.Ok(Detail(list));
    }

    private static async Task<Results<Ok<RateListDetail>, NotFound, ValidationProblem, Conflict<RateListDetail>>> Change(Guid id, ChangeRatesRequest req,
        ReticulaDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var list = await db.RateLists.FirstOrDefaultAsync(r => r.Id == id && r.ArchivedAt == null, ct);
        if (list is null) return TypedResults.NotFound();
        if (list.Version != req.Version) return TypedResults.Conflict(Detail(list));
        var errors = Validate(req.Changes.Select(c => new RateOverride(c.Section, c.Code, c.Rate, c.Date, c.Source, c.Description, c.Unit)).ToList(), time);
        if (errors.Count > 0) return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["changes"] = [.. errors] });
        var unknown = Apply(list, req.Changes.Select(c => new RateOverride(c.Section, c.Code, c.Rate, c.Date, c.Source?.Trim(), c.Description, c.Unit)), req.RateDate, time, user);
        if (unknown.Count > 0) return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["changes"] = [$"Not in this rate list: {string.Join(", ", unknown)}."] });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            var current = await db.RateLists.AsNoTracking().FirstAsync(r => r.Id == id, ct);
            return TypedResults.Conflict(Detail(current));
        }
        return TypedResults.Ok(Detail(list));
    }

    private static List<string> Validate(IReadOnlyList<RateOverride> changes, TimeProvider time)
    {
        var errors = new List<string>();
        if (changes.Count == 0) errors.Add("Give at least one rate.");
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        foreach (var c in changes)
        {
            var name = string.IsNullOrEmpty(c.Code) ? c.Section : $"{c.Section}/{c.Code}";
            if (c.Rate < 0 || double.IsNaN(c.Rate) || double.IsInfinity(c.Rate)) errors.Add($"{name}: the rate must be zero or more.");
            if (c.Date is { } d && d > today.AddDays(1)) errors.Add($"{name}: the rate date cannot be in the future.");
            if (c.Source is { Length: > 200 }) errors.Add($"{name}: the source can be at most 200 characters.");
        }
        return errors;
    }

    private static List<string> Apply(RateList list, IEnumerable<RateOverride> changes, DateOnly? rateDate, TimeProvider time, ClaimsPrincipal user)
    {
        var content = JsonNode.Parse(list.ContentJson)!.AsObject();
        var overrides = JsonNode.Parse(list.OverridesJson)!.AsArray();
        var now = time.GetUtcNow();
        var unknown = RateListContent.Apply(content, overrides, changes, DateOnly.FromDateTime(now.UtcDateTime));
        if (unknown.Count == 0)
            list.Change(content.ToJsonString(), overrides.ToJsonString(), rateDate ?? list.RateDate, Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), now);
        return unknown;
    }

    /// <summary>
    /// A supplier price list as CSV with a header row: code and rate (or price) required; section (default materials),
    /// description, unit, date and source optional. Known rows are applied with their date; new materials are added when
    /// they have a description; anything else is reported.
    /// </summary>
    private static async Task<Results<Ok<ImportResult>, NotFound, ValidationProblem>> Import(Guid id, IFormFile file, ReticulaDbContext db, TimeProvider time,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var list = await db.RateLists.FirstOrDefaultAsync(r => r.Id == id && r.ArchivedAt == null, ct);
        if (list is null) return TypedResults.NotFound();
        if (file.Length is 0 or > 5_000_000) return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Upload a CSV price list of up to 5 MB."] });
        using var reader = new StreamReader(file.OpenReadStream());
        var lines = new List<string[]>();
        while (await reader.ReadLineAsync(ct) is { } line)
            if (line.Trim().Length > 0) lines.Add(SplitCsv(line));
        if (lines.Count < 2) return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["The price list needs a header row and at least one rate."] });
        var header = lines[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => names.Select(n => header.IndexOf(n)).FirstOrDefault(i => i >= 0, -1);
        int code = Col("code", "item", "item code"), rate = Col("rate", "price", "unit price"), section = Col("section"), desc = Col("description"),
            unit = Col("unit", "uom"), date = Col("date", "price date"), source = Col("source", "supplier");
        if (code < 0 || rate < 0) return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["The header needs a code column and a rate (or price) column."] });

        var errors = new List<string>();
        var changes = new List<RateOverride>();
        var defaultSource = Path.GetFileName(file.FileName);
        foreach (var (row, n) in lines.Skip(1).Select((r, i) => (r, i + 2)))
        {
            string Cell(int i) => i >= 0 && i < row.Length ? row[i].Trim() : "";
            if (!double.TryParse(Cell(rate).Replace(" ", "").TrimStart('R'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
            {
                errors.Add($"line {n}: '{Cell(rate)}' is not a rate");
                continue;
            }
            DateOnly? d = null;
            if (Cell(date).Length > 0)
            {
                if (!DateOnly.TryParse(Cell(date), CultureInfo.InvariantCulture, out var parsed)) { errors.Add($"line {n}: '{Cell(date)}' is not a date"); continue; }
                d = parsed;
            }
            changes.Add(new RateOverride(Cell(section) is { Length: > 0 } s ? s : "materials", Cell(code), value, d, Cell(source) is { Length: > 0 } src ? src : defaultSource,
                Cell(desc) is { Length: > 0 } dsc ? dsc : null, Cell(unit) is { Length: > 0 } u ? u : null));
        }
        errors.AddRange(Validate(changes, time).Where(e => !e.StartsWith("Give", StringComparison.Ordinal)));
        var content = JsonNode.Parse(list.ContentJson)!.AsObject();
        var overrides = JsonNode.Parse(list.OverridesJson)!.AsArray();
        var now = time.GetUtcNow();
        var unknown = RateListContent.Apply(content, overrides, changes, DateOnly.FromDateTime(now.UtcDateTime));
        var applied = changes.Count - unknown.Count;
        if (applied > 0)
        {
            list.Change(content.ToJsonString(), overrides.ToJsonString(), list.RateDate, Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), now);
            await db.SaveChangesAsync(ct);
        }
        return TypedResults.Ok(new ImportResult(Detail(list), applied, unknown, errors));
    }

    private static string[] SplitCsv(string line)
    {
        var cells = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cur.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch is ',' or ';') { cells.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        cells.Add(cur.ToString());
        return [.. cells];
    }

    private static async Task<Results<Ok<ProjectRateList>, NotFound>> GetProjectList(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.ArchivedAt == null, ct);
        if (project is null) return TypedResults.NotFound();
        var list = project.RateListId is { } id ? await db.RateLists.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) : null;
        return TypedResults.Ok(new ProjectRateList(list?.Id, list is null ? DesignInputs.DefaultRates : $"{list.Name} (rev {list.Revision})"));
    }

    private static async Task<Results<Ok<ProjectRateList>, NotFound, ValidationProblem>> SetProjectList(Guid projectId, SetProjectRateList req, ReticulaDbContext db,
        TimeProvider time, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.ArchivedAt == null, ct);
        if (project is null) return TypedResults.NotFound();
        RateList? list = null;
        if (req.RateListId is { } id && (list = await db.RateLists.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.ArchivedAt == null, ct)) is null)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["rateListId"] = ["Choose an existing rate list."] });
        project.UseRateList(req.RateListId, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new ProjectRateList(list?.Id, list is null ? DesignInputs.DefaultRates : $"{list.Name} (rev {list.Revision})"));
    }
}
