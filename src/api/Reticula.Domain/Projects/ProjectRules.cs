using System.Text.RegularExpressions;
using NetTopologySuite.Geometries;

namespace Reticula.Domain.Projects;

public static partial class ProjectRules
{
    public const int Srid = 4326;
    public const int NameMaxLength = 200;

    /// <summary>Generous envelope around South Africa, Lesotho and Eswatini (lon/lat).</summary>
    public static readonly Envelope SouthAfrica = new(16.0, 33.5, -35.5, -21.5);

    [GeneratedRegex(@"^[a-z0-9-]+/\d+\.\d+\.\d+$")]
    private static partial Regex RulesRefPattern();

    public static IReadOnlyDictionary<string, string[]> Validate(string? name, string? rulesRef, Polygon? area)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(name))
            errors["name"] = ["Name is required."];
        else if (name.Length > NameMaxLength)
            errors["name"] = [$"Name must be {NameMaxLength} characters or fewer."];

        if (string.IsNullOrWhiteSpace(rulesRef) || !RulesRefPattern().IsMatch(rulesRef))
            errors["rulesRef"] = ["Rules must be given as authority/version, e.g. eskom/0.1.0."];

        if (area is null)
        {
            errors["area"] = ["Area is required."];
        }
        else
        {
            var areaErrors = new List<string>();
            if (area.IsEmpty) areaErrors.Add("Area is empty.");
            if (area.SRID != Srid) areaErrors.Add($"Area must use SRID {Srid} (WGS84).");
            if (!area.IsEmpty && !area.IsValid) areaErrors.Add("Area polygon is not valid: it may cross itself.");
            if (!area.IsEmpty && !SouthAfrica.Contains(area.EnvelopeInternal)) areaErrors.Add("Area must lie within South Africa.");
            if (areaErrors.Count > 0) errors["area"] = [.. areaErrors];
        }

        return errors;
    }
}
