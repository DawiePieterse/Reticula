using System.Text.Json;

namespace Reticula.Infrastructure.Design;

/// <summary>
/// The engineer's options for a design run (calc design.DesignOptions) and an optimisation (calc optimise.OptimiseOptions), checked
/// before they reach the calc service: only known keys, each of the right kind. The calc service checks the values themselves against
/// the rules (a conductor code it does not know, a rating not in the range). The design assistant's proposals pass through the same check.
/// </summary>
public static class RunOptions
{
    private static readonly string[] Constructions = ["overhead", "underground"];
    private static readonly string[] Objectives = ["capex", "lifetime", "spare"];

    private delegate string? Check(JsonElement v);

    private static readonly Dictionary<string, Check> Design = new(StringComparer.Ordinal)
    {
        ["construction"] = v => OneOf(v, [.. Constructions, "compare"]),
        ["mv_construction"] = v => OneOf(v, Constructions),
        ["objective"] = v => OneOf(v, Objectives),
        ["lv_conductors"] = v => MapOf(v, x => x.ValueKind == JsonValueKind.String && x.GetString()!.Length is > 0 and <= 50, "a conductor code"),
        ["mv_conductor"] = v => v.ValueKind == JsonValueKind.Null || v.ValueKind == JsonValueKind.String && v.GetString()!.Length is > 0 and <= 50 ? null : "must be a conductor code",
        ["transformer_ratings"] = v => MapOf(v, x => x.ValueKind == JsonValueKind.Number && x.GetDouble() is > 0 and <= 5000, "a rating in kVA"),
        ["economics"] = v => MapOf(v, x => x.ValueKind == JsonValueKind.Number && double.IsFinite(x.GetDouble()), "a number"),
        ["underground_conditions"] = v => MapOf(v, x => x.ValueKind == JsonValueKind.Number && double.IsFinite(x.GetDouble()), "a number"),
    };

    private static readonly Dictionary<string, Check> Optimise = new(StringComparer.Ordinal)
    {
        ["objectives"] = v => ListOf(v, Objectives, allowEmpty: false),
        ["max_evaluations"] = v => Int(v, 1, 200),
        ["capex_ceiling_pct"] = v => Number(v, 0, 100),
        ["move_radius_m"] = v => Number(v, 0, 500),
        ["moves_per_transformer"] = v => Int(v, 0, 10),
        ["siting"] = v => v.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "must be true or false",
        ["constructions"] = v => v.ValueKind == JsonValueKind.Null ? null : ListOf(v, Constructions, allowEmpty: false),
    };

    public static readonly IReadOnlyCollection<string> DesignKeys = Design.Keys;
    public static readonly IReadOnlyCollection<string> OptimiseKeys = Optimise.Keys;

    /// <summary>Errors by key, empty when the options are acceptable. Null options are the defaults.</summary>
    public static Dictionary<string, string[]> ValidateDesign(JsonElement? options, string prefix = "options") => Validate(options, Design, prefix);

    public static Dictionary<string, string[]> ValidateOptimise(JsonElement? options, string prefix = "optimise") => Validate(options, Optimise, prefix);

    private static Dictionary<string, string[]> Validate(JsonElement? options, Dictionary<string, Check> checks, string prefix)
    {
        var errors = new Dictionary<string, string[]>();
        if (options is not { } o || o.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return errors;
        if (o.ValueKind != JsonValueKind.Object)
        {
            errors[prefix] = ["Must be an object."];
            return errors;
        }
        foreach (var p in o.EnumerateObject())
        {
            if (!checks.TryGetValue(p.Name, out var check)) errors[$"{prefix}.{p.Name}"] = [$"Unknown option. Known: {string.Join(", ", checks.Keys)}."];
            else if (check(p.Value) is { } e) errors[$"{prefix}.{p.Name}"] = [char.ToUpperInvariant(e[0]) + e[1..] + "."];
        }
        return errors;
    }

    private static string? OneOf(JsonElement v, string[] allowed) =>
        v.ValueKind == JsonValueKind.Null || v.ValueKind == JsonValueKind.String && allowed.Contains(v.GetString()) ? null : $"must be one of {string.Join(", ", allowed)}";

    private static string? ListOf(JsonElement v, string[] allowed, bool allowEmpty) =>
        v.ValueKind == JsonValueKind.Array && (allowEmpty || v.GetArrayLength() > 0)
        && v.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && allowed.Contains(x.GetString()))
            ? null : $"must be a list of {string.Join(", ", allowed)}";

    private static string? MapOf(JsonElement v, Func<JsonElement, bool> ok, string what) =>
        v.ValueKind == JsonValueKind.Object && v.EnumerateObject().All(p => p.Name.Length is > 0 and <= 100 && ok(p.Value))
            ? null : $"must map names to {what}";

    private static string? Int(JsonElement v, int min, int max) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i >= min && i <= max ? null : $"must be a whole number from {min} to {max}";

    private static string? Number(JsonElement v, double min, double max) =>
        v.ValueKind == JsonValueKind.Number && v.GetDouble() is var d && d >= min && d <= max ? null : $"must be a number from {min} to {max}";
}
