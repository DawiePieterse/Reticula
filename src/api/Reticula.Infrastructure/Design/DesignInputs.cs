using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Field;
using Reticula.Domain.Layout;
using Reticula.Domain.Projects;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Costing;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Geo;

namespace Reticula.Infrastructure.Design;

/// <summary>
/// A project's inputs to a design run in the calc service's request shape (calc design.DesignRequest without options), with a hash of
/// the whole and of each part. The order of every list is fixed (creation time, then id), so the same project data always gives the
/// same request and the same hash.
/// </summary>
/// <param name="Parts">Hash of each part: rules, candidates, loads, contours, connection_point, rates.</param>
public sealed record DesignInputs(JsonObject Body, string Hash, IReadOnlyDictionary<string, string> Parts, int Candidates, int Loads,
    int LoadsWithoutEstimate, bool ConnectionPointComplete)
{
    /// <summary>What each part's name means, for a stale run's reason.</summary>
    public static readonly IReadOnlyDictionary<string, string> PartNames = new Dictionary<string, string>
    {
        ["rules"] = "the project's rules version changed",
        ["candidates"] = "routes or sites were marked, moved, confirmed or removed",
        ["loads"] = "buildings or their loads changed",
        ["contours"] = "the contours changed",
        ["connection_point"] = "the connection point changed",
        ["rates"] = "the rates changed",
    };

    /// <summary>The design request: these inputs with the engineer's options.</summary>
    public string Request(JsonElement? options)
    {
        var body = (JsonObject)Body.DeepClone();
        body["options"] = options is { ValueKind: JsonValueKind.Object } o ? JsonNode.Parse(o.GetRawText()) : new JsonObject();
        return body.ToJsonString();
    }

    /// <summary>Why a run made from inputs with these part hashes is stale, or null when nothing changed.</summary>
    public string? StaleReason(string partsJson)
    {
        var old = JsonSerializer.Deserialize<Dictionary<string, string>>(partsJson) ?? [];
        var changed = Parts.Where(p => old.GetValueOrDefault(p.Key) != p.Value).Select(p => PartNames.GetValueOrDefault(p.Key, p.Key)).ToList();
        return changed.Count == 0 ? null : "Since this run " + string.Join("; ", changed) + ".";
    }
}

public sealed class DesignInputsBuilder(ReticulaDbContext db, RateService rates)
{
    /// <summary>Candidate kinds the design is built from: the LV network's and the MV routes.</summary>
    public static readonly IReadOnlyList<string> Kinds = [.. LvNetworkService.Kinds, CandidateKinds.MvRoute];

    /// <param name="by">Stored as the importer if the indicative rate library has to be fetched first.</param>
    public async Task<DesignInputs> BuildAsync(Project project, Guid by, CancellationToken ct)
    {
        var o = CalcJson.Options;
        var candidates = await db.Candidates.AsNoTracking()
            .Where(c => c.ProjectId == project.Id && c.ArchivedAt == null && Kinds.Contains(c.Kind))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        var cands = new JsonArray([.. candidates.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id.ToString(),
            ["kind"] = c.Kind,
            ["geometry"] = JsonSerializer.SerializeToNode(GeometryInput.ToDto(c.Geometry), o),
            ["source"] = c.Source,
        })]);

        var (loads, classes) = await ProjectLoads.ReadAsync(db, project.Id, ct);
        var contours = await db.Contours.AsNoTracking().Where(c => c.ProjectId == project.Id).OrderBy(c => c.Id)
            .Select(c => new { c.ElevationM, c.Geometry }).ToListAsync(ct);
        var cont = new JsonArray([.. contours.Select(c => (JsonNode)new JsonObject
        {
            ["elevation_m"] = c.ElevationM,
            ["coordinates"] = new JsonArray([.. c.Geometry.Coordinates.Select(p => (JsonNode)new JsonArray(p.X, p.Y))]),
        })]);

        var cp = await db.ConnectionPoints.AsNoTracking().FirstOrDefaultAsync(c => c.ProjectId == project.Id, ct);
        JsonNode? point = null;
        if (cp is not null)
        {
            point = new JsonObject
            {
                ["coordinates"] = new JsonArray(cp.Location.X, cp.Location.Y),
                ["voltage_kv"] = cp.VoltageKv,
                ["capacity_kva"] = cp.CapacityKva,
                ["fault_3ph_ka"] = cp.Fault3PhKa,
                ["fault_1ph_ka"] = cp.Fault1PhKa,
                ["x_over_r"] = cp.XOverR,
                ["fault_3ph_min_ka"] = cp.Fault3PhMinKa,
            };
        }
        else if (await db.NetworkAssets.AsNoTracking()
                     .Where(a => a.ProjectId == project.Id && a.AssetType == NetworkAssetTypes.ConnectionPoint).OrderBy(a => a.Id)
                     .Select(a => a.Geometry).FirstOrDefaultAsync(ct) is { } g)
        {
            // Only where it is: the authority's capacity and fault level must be entered and sourced (plan 4.1), never taken from a drawing.
            point = new JsonObject { ["coordinates"] = new JsonArray(g.Centroid.X, g.Centroid.Y) };
        }

        var library = await rates.MergedAsync(await rates.ActiveAsync(by, ct), ct);
        var parts = new JsonObject
        {
            ["rules"] = project.RulesRef,
            ["candidates"] = cands,
            ["loads"] = JsonSerializer.SerializeToNode(loads, o),
            ["classes"] = JsonSerializer.SerializeToNode(classes, o),
            ["contours"] = cont,
            ["connection_point"] = point,
            ["rates"] = library,
        };
        string H(params string[] keys) => CanonicalJson.Sha256(string.Join("\n", keys.Select(k => parts[k] is { } n ? CanonicalJson.Write(n.ToJsonString()) : "null")));
        var partHashes = new Dictionary<string, string>
        {
            ["rules"] = H("rules"),
            ["candidates"] = H("candidates"),
            ["loads"] = H("loads", "classes"),
            ["contours"] = H("contours"),
            ["connection_point"] = H("connection_point"),
            ["rates"] = H("rates"),
        };
        return new DesignInputs(parts, CanonicalJson.Hash(parts.ToJsonString()), partHashes, candidates.Count, loads.Count,
            loads.Count(l => l.Kva is null), cp?.IsComplete == true);
    }
}
