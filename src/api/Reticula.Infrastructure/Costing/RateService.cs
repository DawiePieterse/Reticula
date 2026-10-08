using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Costing;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;

namespace Reticula.Infrastructure.Costing;

public sealed record RateImportResult(RateLibrary Library, int Replaced, int Added, int StillIndicative, IReadOnlyList<string> Errors);

/// <summary>
/// The rate library (plan 6.1). One library is active; the engineer's overrides replace single rates on top of it. Before any price list
/// is imported the active library is the calc service's indicative one, stored here so every design run names the exact rates it used.
/// </summary>
public sealed class RateService(ReticulaDbContext db, ICalcClient calc, TimeProvider time)
{
    public Task<RateLibrary?> FindActiveAsync(CancellationToken ct) =>
        db.RateLibraries.AsNoTracking().Where(l => l.Active).OrderByDescending(l => l.ImportedAt).FirstOrDefaultAsync(ct);

    /// <summary>The active library, stored from the calc service's indicative library when none has been imported.</summary>
    public async Task<RateLibrary> ActiveAsync(Guid by, CancellationToken ct)
    {
        if (await FindActiveAsync(ct) is { } lib) return lib;
        using var doc = JsonDocument.Parse(await calc.GetDefaultRatesAsync(ct));
        var r = doc.RootElement;
        lib = new RateLibrary(Guid.CreateVersion7(), r.GetProperty("name").GetString() ?? "indicative",
            DateOnly.TryParse(r.GetProperty("rate_date").GetString(), CultureInfo.InvariantCulture, out var d) ? d : DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime),
            r.GetProperty("source").GetString() ?? "calc service", r.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "ZAR" : "ZAR",
            r.TryGetProperty("indicative", out var ind) && ind.GetBoolean(), r.GetProperty("items").GetRawText(), r.GetProperty("assemblies").GetRawText(),
            by, time.GetUtcNow());
        db.RateLibraries.Add(lib);
        await db.SaveChangesAsync(ct);
        return lib;
    }

    public Task<List<RateOverride>> OverridesAsync(CancellationToken ct) =>
        db.RateOverrides.AsNoTracking().Where(o => o.RemovedAt == null).OrderBy(o => o.ItemCode).ToListAsync(ct);

    /// <summary>The library the calc service prices with (calc cost.RateLibrary): the active library with the live overrides applied.</summary>
    public async Task<JsonObject> MergedAsync(RateLibrary lib, CancellationToken ct)
    {
        var overrides = (await OverridesAsync(ct)).ToDictionary(o => o.ItemCode, StringComparer.Ordinal);
        var items = JsonNode.Parse(lib.ItemsJson)!.AsArray();
        var applied = 0;
        foreach (var item in items.OfType<JsonObject>())
        {
            if (item["code"]?.GetValue<string>() is not { } code || !overrides.TryGetValue(code, out var o)) continue;
            item["rate"] = o.Rate;
            item["rate_date"] = o.RateDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            item["source"] = $"engineer's rate: {o.Source}";
            applied++;
        }
        return new JsonObject
        {
            ["name"] = applied == 0 ? lib.Name : $"{lib.Name} with {applied} engineer's rate{(applied == 1 ? "" : "s")}",
            ["rate_date"] = lib.RateDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["source"] = lib.Source,
            ["currency"] = lib.Currency,
            ["indicative"] = lib.Indicative,
            ["items"] = items,
            ["assemblies"] = JsonNode.Parse(lib.AssembliesJson),
        };
    }

    /// <summary>
    /// A new active library from a CSV price list (code, description, unit, rate, and optionally category and uncertainty_pct): its rates
    /// replace or add to the active library's items; assemblies and items it does not list are kept. Nothing is saved when a row is invalid.
    /// </summary>
    public async Task<RateImportResult> ImportCsvAsync(string csv, string name, DateOnly rateDate, string source, Guid by, CancellationToken ct)
    {
        var current = await ActiveAsync(by, ct);
        var errors = new List<string>();
        var rows = Csv.Read(csv);
        if (rows.Count < 2) return new RateImportResult(current, 0, 0, 0, ["The file has no rate rows under a header row."]);
        var head = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string n) => head.IndexOf(n);
        var (code, desc, unit, rate, cat, unc) = (Col("code"), Col("description"), Col("unit"), Col("rate"), Col("category"), Col("uncertainty_pct"));
        foreach (var (n, i) in new[] { ("code", code), ("description", desc), ("unit", unit), ("rate", rate) })
            if (i < 0) errors.Add($"The header has no '{n}' column.");
        if (errors.Count > 0) return new RateImportResult(current, 0, 0, 0, errors);

        var items = JsonNode.Parse(current.ItemsJson)!.AsArray().OfType<JsonObject>().ToList();
        var byCode = items.ToDictionary(i => i["code"]!.GetValue<string>(), StringComparer.Ordinal);
        var date = rateDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int replaced = 0, added = 0;
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            string Cell(int i) => i >= 0 && i < row.Count ? row[i].Trim() : "";
            var c = Cell(code);
            if (c.Length == 0) { errors.Add($"Row {r + 1}: no code."); continue; }
            if (!seen.Add(c)) { errors.Add($"Row {r + 1}: code {c} is listed twice."); continue; }
            if (!double.TryParse(Cell(rate), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < 0)
            { errors.Add($"Row {r + 1}: rate '{Cell(rate)}' is not a number of zero or more."); continue; }
            double? u = null;
            if (Cell(unc) is { Length: > 0 } us)
            {
                if (!double.TryParse(us, NumberStyles.Float, CultureInfo.InvariantCulture, out var uv) || uv is < 0 or > 100)
                { errors.Add($"Row {r + 1}: uncertainty_pct '{us}' is not between 0 and 100."); continue; }
                u = uv;
            }
            var item = new JsonObject
            {
                ["code"] = c,
                ["description"] = Cell(desc) is { Length: > 0 } d ? d : byCode.GetValueOrDefault(c)?["description"]?.GetValue<string>() ?? c,
                ["unit"] = Cell(unit) is { Length: > 0 } un ? un : byCode.GetValueOrDefault(c)?["unit"]?.GetValue<string>() ?? "each",
                ["rate"] = value,
                ["category"] = Cell(cat) is { Length: > 0 } ca ? ca : byCode.GetValueOrDefault(c)?["category"]?.GetValue<string>() ?? "material",
                ["rate_date"] = date,
                ["source"] = source,
                ["uncertainty_pct"] = u,
            };
            if (byCode.ContainsKey(c)) replaced++;
            else added++;
            byCode[c] = item;
        }
        if (errors.Count > 0) return new RateImportResult(current, 0, 0, 0, errors);

        var stillIndicative = current.Indicative ? items.Count(i => !seen.Contains(i["code"]!.GetValue<string>())) : 0;
        var merged = new JsonArray([.. byCode.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (JsonNode)kv.Value.DeepClone())]);
        var now = time.GetUtcNow();
        var lib = new RateLibrary(Guid.CreateVersion7(), name, rateDate, source, current.Currency, stillIndicative > 0, merged.ToJsonString(),
            current.AssembliesJson, by, now);
        foreach (var old in await db.RateLibraries.Where(l => l.Active).ToListAsync(ct)) old.Deactivate();
        db.RateLibraries.Add(lib);
        await db.SaveChangesAsync(ct);
        return new RateImportResult(lib, replaced, added, stillIndicative, []);
    }
}
