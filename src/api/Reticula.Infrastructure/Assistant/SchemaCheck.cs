using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reticula.Infrastructure.Assistant;

/// <summary>
/// Checks a tool call's input against its schema before anything runs (plan 8.1): the subset of JSON Schema the tools use (type,
/// properties, required, additionalProperties: false, enum, maxLength, minimum, maximum, items). Anything the schema does not allow is refused.
/// </summary>
public static class SchemaCheck
{
    public static List<string> Errors(JsonObject schema, JsonNode? value)
    {
        var errors = new List<string>();
        Check(schema, value, "input", errors);
        return errors;
    }

    private static void Check(JsonObject schema, JsonNode? value, string path, List<string> errors)
    {
        var type = schema["type"]?.GetValue<string>();
        var kind = value?.GetValueKind() ?? JsonValueKind.Null;
        bool Is(params JsonValueKind[] kinds) => kinds.Contains(kind);
        var ok = type switch
        {
            "object" => Is(JsonValueKind.Object),
            "array" => Is(JsonValueKind.Array),
            "string" => Is(JsonValueKind.String),
            "number" => Is(JsonValueKind.Number),
            "integer" => Is(JsonValueKind.Number) && value!.GetValue<double>() % 1 == 0,
            "boolean" => Is(JsonValueKind.True, JsonValueKind.False),
            null => true,
            _ => false,
        };
        if (!ok)
        {
            errors.Add($"{path} must be {type}.");
            return;
        }
        if (schema["enum"] is JsonArray allowed && !allowed.Any(a => JsonNode.DeepEquals(a, value)))
            errors.Add($"{path} must be one of {string.Join(", ", allowed.Select(a => a?.ToJsonString()))}.");
        if (type == "string" && schema["maxLength"]?.GetValue<int>() is { } max && value!.GetValue<string>().Length > max)
            errors.Add($"{path} is longer than {max} characters.");
        if (type is "number" or "integer")
        {
            var d = value!.GetValue<double>();
            if (schema["minimum"]?.GetValue<double>() is { } lo && d < lo) errors.Add($"{path} is below {lo}.");
            if (schema["maximum"]?.GetValue<double>() is { } hi && d > hi) errors.Add($"{path} is above {hi}.");
        }
        if (type == "array" && schema["items"] is JsonObject items)
        {
            var i = 0;
            foreach (var x in value!.AsArray()) Check(items, x, $"{path}[{i++}]", errors);
        }
        if (type != "object") return;
        var obj = value!.AsObject();
        var props = schema["properties"] as JsonObject ?? [];
        foreach (var req in schema["required"]?.AsArray().Select(r => r!.GetValue<string>()) ?? [])
            if (!obj.ContainsKey(req)) errors.Add($"{path}.{req} is required.");
        foreach (var (name, v) in obj)
        {
            if (props[name] is JsonObject p) Check(p, v, $"{path}.{name}", errors);
            else if (schema["additionalProperties"] is JsonObject extra) Check(extra, v, $"{path}.{name}", errors);
            else if (schema["additionalProperties"]?.GetValue<bool>() != true) errors.Add($"{path}.{name} is not allowed.");
        }
    }
}
