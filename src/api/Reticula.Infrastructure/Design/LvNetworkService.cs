using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Reticula.Domain.Design;
using Reticula.Domain.Field;
using Reticula.Domain.Projects;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Geo;

namespace Reticula.Infrastructure.Design;

/// <summary>Builds a project's LV network model in the calc service from the marked routes and sites, and stores it (plan 2.1).</summary>
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

        var network = new LvNetwork(Guid.CreateVersion7(), project.Id, result.RulesRef, result.RulesHash, result.Clause,
            JsonSerializer.Serialize(result.Summary, Json), JsonSerializer.Serialize(result.Feeders, Json), JsonSerializer.Serialize(result.Issues, Json),
            result.Issues.Count(i => i.Severity == "error"), userId, time.GetUtcNow());
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
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return network;
    }

    /// <summary>Why the stored network no longer matches the project, or null when it does.</summary>
    public async Task<string?> StaleReasonAsync(Project project, LvNetwork network, CancellationToken ct)
    {
        if (project.RulesRef != network.RulesRef) return $"The project now uses rules {project.RulesRef}; the network was built with {network.RulesRef}.";
        var changed = await db.Candidates.AnyAsync(c => c.ProjectId == project.Id && Kinds.Contains(c.Kind) && c.UpdatedAt > network.BuiltAt, ct);
        return changed ? "LV routes or sites were marked, moved or removed after the network was built." : null;
    }
}
