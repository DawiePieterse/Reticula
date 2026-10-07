using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Reticula.Infrastructure.Design;

/// <summary>
/// JSON in one canonical form, for hashes that compare content rather than formatting: object keys sorted (ordinal), no
/// whitespace, every number written as its IEEE double ("R" round-trip form). Postgres jsonb reorders keys and the calc
/// service may format numbers differently over versions; two results hash the same exactly when their values are the same.
/// </summary>
public static class CanonicalJson
{
    public static string Write(JsonElement e)
    {
        using var buf = new MemoryStream();
        using (var w = new Utf8JsonWriter(buf, new JsonWriterOptions { Indented = false, SkipValidation = false }))
            WriteTo(w, e);
        return Encoding.UTF8.GetString(buf.ToArray());
    }

    public static string Write(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Write(doc.RootElement);
    }

    /// <summary>SHA-256 of the canonical form, lower-case hex.</summary>
    public static string Hash(JsonElement e) => Sha256(Write(e));

    public static string Hash(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Hash(doc.RootElement);
    }

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void WriteTo(Utf8JsonWriter w, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(p.Name);
                    WriteTo(w, p.Value);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var x in e.EnumerateArray()) WriteTo(w, x);
                w.WriteEndArray();
                break;
            case JsonValueKind.Number:
                // Through the raw value: 1, 1.0 and 1e0 are one double.
                var d = double.Parse(e.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
                w.WriteRawValue(d.ToString("R", CultureInfo.InvariantCulture), skipInputValidation: true);
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }

    /// <summary>JSON paths at which two documents differ, up to <paramref name="max"/>, for a failed reproduction (plan 7.2).</summary>
    public static List<string> Differences(JsonElement a, JsonElement b, int max = 20)
    {
        var out_ = new List<string>();
        Diff(a, b, "$", out_, max);
        return out_;
    }

    private static void Diff(JsonElement a, JsonElement b, string path, List<string> out_, int max)
    {
        if (out_.Count >= max) return;
        if (a.ValueKind != b.ValueKind)
        {
            out_.Add(path);
            return;
        }
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var bp = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                foreach (var p in a.EnumerateObject())
                {
                    if (bp.Remove(p.Name, out var v)) Diff(p.Value, v, $"{path}.{p.Name}", out_, max);
                    else out_.Add($"{path}.{p.Name}");
                    if (out_.Count >= max) return;
                }
                foreach (var name in bp.Keys.Take(max - out_.Count)) out_.Add($"{path}.{name}");
                break;
            case JsonValueKind.Array:
                var (la, lb) = (a.GetArrayLength(), b.GetArrayLength());
                if (la != lb) out_.Add($"{path}.length");
                for (var i = 0; i < Math.Min(la, lb) && out_.Count < max; i++) Diff(a[i], b[i], $"{path}[{i}]", out_, max);
                break;
            default:
                if (Write(a) != Write(b)) out_.Add(path);
                break;
        }
    }
}
