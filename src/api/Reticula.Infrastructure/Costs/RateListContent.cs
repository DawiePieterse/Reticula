using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reticula.Infrastructure.Costs;

/// <summary>One dated change to a rate (plan 6.1): from the engineer or a supplier price list.</summary>
public sealed record RateOverride(string Section, string? Code, double Rate, DateOnly? Date, string? Source, string? Description = null, string? Unit = null);

/// <summary>A rate as listed for editing. An assembly's rate is the sum of its components (display only).</summary>
public sealed record RateRow(string Section, string Code, string? Description, string Unit, double? Rate, string? Assembly, double? AssemblyRate,
    JsonElement? Override);

/// <summary>Reads and changes the calc service's rate-list format (snake_case JSON).</summary>
public static class RateListContent
{
    /// <summary>Sections of code → rate (or {assembly}), with their unit.</summary>
    public static readonly IReadOnlyDictionary<string, string> MapSections = new Dictionary<string, string>
    {
        ["conductor_per_m"] = "m", ["pole_each"] = "each", ["transformer_each"] = "each", ["mv_conductor_per_m"] = "m",
        ["pole_mount_each"] = "each", ["minisub_each"] = "each",
    };

    /// <summary>Single rates (or {assembly}).</summary>
    public static readonly IReadOnlyDictionary<string, string> ScalarSections = new Dictionary<string, string>
    {
        ["stay_each"] = "each", ["trench_per_m"] = "m", ["kiosk_each"] = "each", ["service_overhead_each"] = "each",
        ["service_underground_each"] = "each", ["mv_pole_per_km"] = "km",
    };

    public static List<RateRow> Rows(JsonObject content, JsonArray overrides)
    {
        var ov = overrides.OfType<JsonObject>().ToDictionary(o => Key(o["section"]!.GetValue<string>(), o["code"]?.GetValue<string>()), o => JsonSerializer.SerializeToElement(o));
        var materials = content["materials"] as JsonObject ?? [];
        var rows = new List<RateRow>();
        foreach (var (section, unit) in MapSections)
            if (content[section] is JsonObject map)
                foreach (var (code, value) in map)
                    rows.Add(Row(section, code, null, unit, value, content, ov));
        foreach (var (section, unit) in ScalarSections)
            if (content[section] is { } value)
                rows.Add(Row(section, "", null, unit, value, content, ov));
        foreach (var (code, m) in materials)
            rows.Add(new RateRow("materials", code, m?["description"]?.GetValue<string>(), m?["unit"]?.GetValue<string>() ?? "each", m?["rate"]?.GetValue<double>(), null, null,
                ov.TryGetValue(Key("materials", code), out var e) ? e : null));
        return rows;
    }

    private static RateRow Row(string section, string code, string? description, string unit, JsonNode? value, JsonObject content, Dictionary<string, JsonElement> ov)
    {
        string? assembly = value is JsonObject o ? o["assembly"]?.GetValue<string>() : null;
        double? rate = value is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        return new RateRow(section, code, assembly is null ? description : content["assemblies"]?[assembly]?["description"]?.GetValue<string>(), unit, rate, assembly,
            assembly is null ? null : AssemblyRate(content, assembly), ov.TryGetValue(Key(section, code), out var e) ? e : null);
    }

    public static double? AssemblyRate(JsonObject content, string code)
    {
        if (content["assemblies"]?[code]?["components"] is not JsonArray parts) return null;
        double total = 0;
        foreach (var p in parts)
        {
            if (p is null) return null;
            var rate = content["materials"]?[p["material"]!.GetValue<string>()]?["rate"]?.GetValue<double>();
            if (rate is null) return null;
            total += rate.Value * p!["qty"]!.GetValue<double>();
        }
        return Math.Round(total, 2);
    }

    /// <summary>Applies overrides; returns the codes that are not in the list (left unchanged).</summary>
    public static List<string> Apply(JsonObject content, JsonArray overrides, IEnumerable<RateOverride> changes, DateOnly today)
    {
        var unknown = new List<string>();
        foreach (var c in changes)
        {
            var code = c.Code ?? "";
            JsonNode? previous;
            if (ScalarSections.ContainsKey(c.Section))
            {
                previous = content[c.Section]?.DeepClone();
                content[c.Section] = c.Rate;
            }
            else if (MapSections.ContainsKey(c.Section) && content[c.Section] is JsonObject map && map.ContainsKey(code))
            {
                previous = map[code]?.DeepClone();
                map[code] = c.Rate;
            }
            else if (c.Section == "materials")
            {
                var materials = content["materials"] as JsonObject ?? (JsonObject)(content["materials"] = new JsonObject());
                if (materials[code] is JsonObject m)
                {
                    previous = m["rate"]?.DeepClone();
                    m["rate"] = c.Rate;
                }
                else if (!string.IsNullOrWhiteSpace(c.Description))
                {
                    previous = null;
                    materials[code] = new JsonObject { ["description"] = c.Description, ["unit"] = c.Unit ?? "each", ["rate"] = c.Rate };
                }
                else
                {
                    unknown.Add($"materials/{code}");
                    continue;
                }
            }
            else
            {
                unknown.Add(code.Length > 0 ? $"{c.Section}/{code}" : c.Section);
                continue;
            }
            var key = Key(c.Section, code);
            foreach (var old in overrides.OfType<JsonObject>().Where(o => Key(o["section"]!.GetValue<string>(), o["code"]?.GetValue<string>()) == key).ToList())
                overrides.Remove(old);
            overrides.Add(new JsonObject
            {
                ["section"] = c.Section, ["code"] = code, ["rate"] = c.Rate, ["previous"] = previous,
                ["date"] = (c.Date ?? today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["source"] = c.Source,
            });
        }
        content["overrides"] = overrides.DeepClone();
        return unknown;
    }

    private static string Key(string section, string? code) => $"{section}/{code ?? ""}";
}
