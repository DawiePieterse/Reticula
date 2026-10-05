using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Auth;
using Reticula.Domain.Design;
using Reticula.Domain.Projects;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;

namespace Reticula.Api.Design;

public sealed record LvNodeDto(string Id, string Kind, double[] Coordinates, string? Label, Guid? CandidateId, string? Feeder, double? DistanceM);

public sealed record LvBranchDto(string Id, string Kind, string FromNode, string ToNode, double[][] Coordinates, double LengthM, Guid? CandidateId, string? Feeder);

/// <param name="Stale">Why the network no longer matches the marked routes and sites or the project's rules; null when it does.</param>
public sealed record LvNetworkDto(Guid Id, string RulesRef, string RulesHash, string Clause, DateTimeOffset BuiltAt, string? Stale,
    LvSummary Summary, IReadOnlyList<LvFeeder> Feeders, IReadOnlyList<LvIssue> Issues, IReadOnlyList<LvNodeDto> Nodes, IReadOnlyList<LvBranchDto> Branches);

/// <param name="Network">The project's LV network, or null before it is first built.</param>
public sealed record LvNetworkStatus(LvNetworkDto? Network);

/// <summary>The LV network model (plan 2.1): built by the calc service from the marked LV routes and sites, stored per project.</summary>
public static class LvNetworkEndpoints
{
    public static IEndpointRouteBuilder MapLvNetworkEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{projectId:guid}/lv-network").WithTags("Design").RequireAuthorization(Policies.FieldUser);
        g.MapGet("/", Get);
        g.MapPost("/", Build).RequireAuthorization(Policies.Engineer);
        return app;
    }

    private static async Task<Results<Ok<LvNetworkStatus>, NotFound>> Get(Guid projectId, ReticulaDbContext db, LvNetworkService service, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.ArchivedAt == null, ct);
        if (project is null) return TypedResults.NotFound();
        var network = await db.LvNetworks.AsNoTracking().FirstOrDefaultAsync(n => n.ProjectId == projectId, ct);
        return TypedResults.Ok(new LvNetworkStatus(network is null ? null : await ToDtoAsync(db, service, project, network, ct)));
    }

    /// <summary>Builds the network again from the routes and sites marked now, replacing the stored one.</summary>
    private static async Task<Results<Ok<LvNetworkDto>, NotFound, ValidationProblem>> Build(
        Guid projectId, ReticulaDbContext db, LvNetworkService service, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.ArchivedAt == null, ct);
        if (project is null) return TypedResults.NotFound();
        LvNetwork network;
        try
        {
            network = await service.BuildAsync(project, Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), ct);
        }
        catch (CalcRejectedException e)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["rulesRef"] = [e.Message] });
        }
        return TypedResults.Ok(await ToDtoAsync(db, service, project, network, ct));
    }

    /// <summary>Nodes and branches in the calc service's order: N1, N2, … N10.</summary>
    private static async Task<LvNetworkDto> ToDtoAsync(ReticulaDbContext db, LvNetworkService service, Project project, LvNetwork n, CancellationToken ct)
    {
        var nodes = await db.LvNodes.AsNoTracking().Where(x => x.NetworkId == n.Id).OrderBy(x => x.Key.Length).ThenBy(x => x.Key).ToListAsync(ct);
        var branches = await db.LvBranches.AsNoTracking().Where(x => x.NetworkId == n.Id).OrderBy(x => x.Key.Length).ThenBy(x => x.Key).ToListAsync(ct);
        var json = LvNetworkService.Json;
        return new LvNetworkDto(n.Id, n.RulesRef, n.RulesHash, n.Clause, n.BuiltAt, await service.StaleReasonAsync(project, n, ct),
            JsonSerializer.Deserialize<LvSummary>(n.SummaryJson, json)!,
            JsonSerializer.Deserialize<List<LvFeeder>>(n.FeedersJson, json) ?? [],
            JsonSerializer.Deserialize<List<LvIssue>>(n.IssuesJson, json) ?? [],
            [.. nodes.Select(x => new LvNodeDto(x.Key, x.Kind, [x.Geometry.X, x.Geometry.Y], x.Label, x.CandidateId, x.Feeder, x.DistanceM))],
            [.. branches.Select(x => new LvBranchDto(x.Key, x.Kind, x.FromKey, x.ToKey, [.. x.Geometry.Coordinates.Select(c => new[] { c.X, c.Y })],
                x.LengthM, x.CandidateId, x.Feeder))]);
    }
}
