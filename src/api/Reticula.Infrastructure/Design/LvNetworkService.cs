using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Reticula.Domain.Design;
using Reticula.Domain.Field;
using Reticula.Domain.Layout;
using Reticula.Domain.Projects;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Geo;

namespace Reticula.Infrastructure.Design;

/// <summary>
/// Builds a project's LV network model in the calc service from the marked routes and sites (plan 2.1), connects each building's
/// load to it and gives it a phase (plan 2.2), and stores the result.
/// </summary>
public sealed class LvNetworkService(ReticulaDbContext db, ICalcClient calc, TimeProvider time)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Candidate kinds the network is built from. MV routes belong to Phase 3.</summary>
    public static readonly IReadOnlyList<string> Kinds = [CandidateKinds.LvRoute, CandidateKinds.Transformer, CandidateKinds.MiniSub, CandidateKinds.Pole];

    /// <exception cref="CalcRejectedException">The project's rules file has no LV network tolerances.</exception>
    public async Task<LvNetwork> BuildAsync(Project project, Guid userId, CancellationToken ct)
    {
        var candidates = await db.Candidates.AsNoTracking()
            .Where(c => c.ProjectId == project.Id && c.ArchivedAt == null && Kinds.Contains(c.Kind))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToListAsync(ct);
        var input = candidates.Select(c => new LvCandidate(c.Id.ToString(), c.Kind, JsonSerializer.SerializeToElement(GeometryInput.ToDto(c.Geometry), Json))).ToList();
        var result = await calc.BuildLvNetworkAsync(project.RulesRef, input, ct);

        var (buildings, classes) = await LoadInputsAsync(project.Id, ct);
        CalcLvLoads? loads = null;
        CalcLvAnalysis? analysis = null;
        var issues = result.Issues.ToList();
        try
        {
            loads = await calc.AllocateLvLoadsAsync(project.RulesRef, result, buildings, ct);
            issues.AddRange(loads.Issues);
        }
        catch (CalcRejectedException e)
        {
            // Rules from before plan 2.2 have no service or phasing settings: keep the network, say why loads are missing.
            issues.Add(new LvIssue("warning", "loads_skipped", $"Loads were not connected: {e.Message}.", 1, [], []));
        }
        if (loads is not null)
        {
            try
            {
                var at = loads.Allocations.Select(a => new LvLoadAt(a.LoadId, a.Branch, a.OffsetM, a.Phase, a.Kva, a.Kind,
                    classes.GetValueOrDefault(a.LoadId), a.Label)).ToList();
                analysis = await calc.AnalyseLvAsync(project.RulesRef, result, at, ct);
                issues.AddRange(analysis.Issues);
            }
            catch (CalcRejectedException e)
            {
                // Rules from before plan 2.4 have no design check settings.
                issues.Add(new LvIssue("warning", "checks_skipped", $"Voltage drop, loading and fault level were not checked: {e.Message}.", 1, [], []));
            }
        }

        var network = new LvNetwork(Guid.CreateVersion7(), project.Id, result.RulesRef, result.RulesHash, result.Clause,
            JsonSerializer.Serialize(result.Summary, Json), JsonSerializer.Serialize(result.Feeders, Json), JsonSerializer.Serialize(issues, Json),
            issues.Count(i => i.Severity == "error"), userId, time.GetUtcNow(),
            loads?.Clause, loads is null ? null : JsonSerializer.Serialize(loads.Summary, Json), JsonSerializer.Serialize(loads?.Feeders ?? [], Json),
            JsonSerializer.Serialize(loads?.Boxes ?? [], Json), analysis is null ? null : JsonSerializer.Serialize(analysis, Json));
        var known = candidates.Select(c => c.Id).ToHashSet();
        Guid? Candidate(string? id) => Guid.TryParse(id, out var g) && known.Contains(g) ? g : null;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LvNetworks.Where(n => n.ProjectId == project.Id).ExecuteDeleteAsync(ct);
        db.LvNetworks.Add(network);
        foreach (var n in result.Nodes)
            db.LvNodes.Add(new LvNode(Guid.CreateVersion7(), network.Id, n.Id, n.Kind,
                PolygonDto.Factory.CreatePoint(new Coordinate(n.Coordinates[0], n.Coordinates[1])), n.Label, Candidate(n.CandidateId), n.Feeder, n.DistanceM));
        foreach (var b in result.Branches)
            db.LvBranches.Add(new LvBranch(Guid.CreateVersion7(), network.Id, b.Id, b.Kind, b.FromNode, b.ToNode,
                PolygonDto.Factory.CreateLineString([.. b.Coordinates.Select(c => new Coordinate(c[0], c[1]))]), b.LengthM,
                Candidate(b.CandidateId), b.Feeder));
        var from = buildings.ToDictionary(x => x.Id);
        foreach (var a in loads?.Allocations ?? [])
        {
            var building = from[a.LoadId].Coordinates;
            db.LvLoads.Add(new LvLoad(Guid.CreateVersion7(), network.Id, Guid.Parse(a.LoadId), Guid.Parse(a.BuildingId), a.Label, a.Kind, a.Kva,
                a.Branch, a.Node, a.OffsetM, a.ServiceM, a.Box, a.Feeder, a.DistanceM, a.Phase,
                PolygonDto.Factory.CreateLineString([new Coordinate(building[0], building[1]), new Coordinate(a.At[0], a.At[1])])));
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return network;
    }

    /// <summary>Why the stored network no longer matches the project, or null when it does.</summary>
    public async Task<string?> StaleReasonAsync(Project project, LvNetwork network, CancellationToken ct)
    {
        if (project.RulesRef != network.RulesRef) return $"The project now uses rules {project.RulesRef}; the network was built with {network.RulesRef}.";
        var changed = await db.Candidates.AnyAsync(c => c.ProjectId == project.Id && Kinds.Contains(c.Kind) && c.UpdatedAt > network.BuiltAt, ct);
        if (changed) return "LV routes or sites were marked, moved or removed after the network was built.";
        var loads = await db.LoadPoints.AnyAsync(l => l.ProjectId == project.Id && l.UpdatedAt > network.BuiltAt, ct)
            || await db.Buildings.AnyAsync(b => b.ProjectId == project.Id && b.UpdatedAt > network.BuiltAt, ct);
        if (!loads && network.LoadsSummaryJson is { } json)
        {
            // Buildings removed by a new import leave no newer row behind; the count gives them away.
            var built = JsonSerializer.Deserialize<LvLoadSummary>(json, Json)!.Loads;
            loads = built != await db.Buildings.CountAsync(b => b.ProjectId == project.Id && b.Status != BuildingStatus.NotPresent, ct);
        }
        return loads ? "Buildings or their loads changed after the network was built." : null;
    }

    /// <summary>
    /// Every building still standing, with its load when it has one, and each residential load's Herman-Beta class by load id.
    /// Buildings without a load are reported, not guessed.
    /// </summary>
    private async Task<(List<LvLoadIn> Inputs, Dictionary<string, string?> Classes)> LoadInputsAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await (
            from b in db.Buildings.AsNoTracking()
            where b.ProjectId == projectId && b.Status != BuildingStatus.NotPresent
            join s in db.Stands.AsNoTracking() on b.StandId equals s.Id into ss
            from s in ss.DefaultIfEmpty()
            join l in db.LoadPoints.AsNoTracking() on b.Id equals l.BuildingId into ls
            from l in ls.DefaultIfEmpty()
            orderby b.Id
            select new { b.Id, b.Location, Erf = s == null ? null : s.ErfNumber, Load = l }).ToListAsync(ct);
        var inputs = rows.Select(x => new LvLoadIn((x.Load?.Id ?? x.Id).ToString(), x.Id.ToString(), x.Erf, [x.Location.X, x.Location.Y],
            x.Load?.Kva, x.Load?.Kind ?? LoadKinds.Residential)).ToList();
        // A residential load point's category is its load class (or the engineer's override); special loads have none.
        var classes = rows.Where(x => x.Load is { Kind: LoadKinds.Residential })
            .ToDictionary(x => x.Load!.Id.ToString(), x => x.Load!.ClassOverride ?? x.Load!.Category);
        return (inputs, classes);
    }
}
