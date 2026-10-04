using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Reticula.Api.Jobs;
using Reticula.Domain.Auth;
using Reticula.Domain.Design;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;
using Reticula.Infrastructure.Geo;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Api.Design;

public sealed record ConnectionPointRequest(double Lon, double Lat, double VoltageKv, double? AvailableCapacityKva, double? FaultMvaMax,
    double? FaultMvaMin, double? XR, double? SendingVoltagePct, string? Reference);

public sealed record ConnectionPointDto(double Lon, double Lat, double VoltageKv, double? AvailableCapacityKva, double? FaultMvaMax,
    double? FaultMvaMin, double? XR, double? SendingVoltagePct, string? Reference, IReadOnlyList<string> Missing, DateTimeOffset UpdatedAt)
{
    public static ConnectionPointDto From(ConnectionPoint c) => new(c.Location.X, c.Location.Y, c.VoltageKv, c.AvailableCapacityKva, c.FaultMvaMax,
        c.FaultMvaMin, c.XR, c.SendingVoltagePct, c.Reference, c.MissingForStudy(), c.UpdatedAt);
}

/// <summary>Which MV design to study; the latest finished one when empty.</summary>
public sealed record BulkStudyRequest(Guid? MvDesignRunId);

public static class BulkEndpoints
{
    /// <summary>How far (degrees, about 1 m) the MV design's supply may be from the connection point.</summary>
    private const double SameSupply = 1e-5;

    public static IEndpointRouteBuilder MapBulkEndpoints(this IEndpointRouteBuilder app)
    {
        var cp = app.MapGroup("/api/projects/{projectId:guid}/connection-point").WithTags("Bulk supply").RequireAuthorization(Policies.FieldUser);
        cp.MapGet("/", GetPoint);
        cp.MapPut("/", PutPoint).RequireAuthorization(Policies.Engineer);

        var g = app.MapGroup("/api/projects/{projectId:guid}/bulk-studies").WithTags("Bulk supply").RequireAuthorization(Policies.FieldUser);
        g.MapPost("/", Start).RequireAuthorization(Policies.Engineer);
        g.MapGet("/", (Guid projectId, ReticulaDbContext db, CancellationToken ct) => DesignEndpoints.ListKind(projectId, DesignKinds.Bulk, db, ct));
        g.MapGet("/{runId:guid}", (Guid projectId, Guid runId, ReticulaDbContext db, CancellationToken ct) => DesignEndpoints.Get(projectId, runId, DesignKinds.Bulk, db, ct));
        return app;
    }

    private static async Task<Results<Ok<ConnectionPointDto?>, NotFound>> GetPoint(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)) return TypedResults.NotFound();
        var c = await db.ConnectionPoints.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId, ct);
        return TypedResults.Ok<ConnectionPointDto?>(c is null ? null : ConnectionPointDto.From(c));
    }

    private static async Task<Results<Ok<ConnectionPointDto>, NotFound, ValidationProblem>> PutPoint(
        Guid projectId, ConnectionPointRequest req, ReticulaDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)) return TypedResults.NotFound();
        var errors = new Dictionary<string, string[]>();
        if (req.Lon is < -180 or > 180 || req.Lat is < -90 or > 90) errors["location"] = ["The location must be a longitude and latitude in WGS84."];
        if (req.VoltageKv is <= 0 or > 132) errors["voltageKv"] = ["The supply voltage must be more than 0 and at most 132 kV."];
        if (req.AvailableCapacityKva is <= 0) errors["availableCapacityKva"] = ["The available capacity must be more than 0 kVA."];
        if (req.FaultMvaMax is <= 0) errors["faultMvaMax"] = ["The maximum fault level must be more than 0 MVA."];
        if (req.FaultMvaMin is <= 0) errors["faultMvaMin"] = ["The minimum fault level must be more than 0 MVA."];
        else if (req.FaultMvaMin is { } min && req.FaultMvaMax is { } max && min > max) errors["faultMvaMin"] = ["The minimum fault level cannot exceed the maximum."];
        if (req.XR is <= 0) errors["xr"] = ["X/R must be more than 0."];
        if (req.SendingVoltagePct is < 90 or > 110) errors["sendingVoltagePct"] = ["The sending voltage must be between 90 and 110 % of nominal."];
        if (req.Reference is { Length: > 200 }) errors["reference"] = ["The reference can be at most 200 characters."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var now = time.GetUtcNow();
        var location = PolygonDto.Factory.CreatePoint(new Coordinate(req.Lon, req.Lat));
        var c = await db.ConnectionPoints.FirstOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if (c is null)
        {
            c = new ConnectionPoint(projectId, location, req.VoltageKv, userId, now);
            db.ConnectionPoints.Add(c);
        }
        c.Update(location, req.VoltageKv, req.AvailableCapacityKva, req.FaultMvaMax, req.FaultMvaMin, req.XR, req.SendingVoltagePct,
            string.IsNullOrWhiteSpace(req.Reference) ? null : req.Reference.Trim(), userId, now);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ConnectionPointDto.From(c));
    }

    /// <summary>Hard stop (plan 4.1): no study without the authority's capacity and fault level, or on an MV design fed from elsewhere.</summary>
    private static async Task<Results<Accepted<StartedDesign>, NotFound, ValidationProblem>> Start(
        Guid projectId, BulkStudyRequest req, ReticulaDbContext db, IJobQueue queue, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.ArchivedAt == null, ct);
        if (project is null) return TypedResults.NotFound();
        var errors = new Dictionary<string, string[]>();
        var cp = await db.ConnectionPoints.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId, ct);
        if (cp is null) errors["connectionPoint"] = ["Enter the authority's connection point before the bulk supply study."];
        else if (cp.MissingForStudy() is { Count: > 0 } missing)
            errors["connectionPoint"] = [$"The bulk supply study needs the authority's {string.Join(" and ", missing)} at the connection point."];

        var runs = db.DesignRuns.AsNoTracking().Where(r => r.ProjectId == projectId && r.Kind == DesignKinds.Mv && r.Status == DesignRunStatus.Succeeded);
        var mv = req.MvDesignRunId is { } id
            ? await runs.FirstOrDefaultAsync(r => r.Id == id, ct)
            : await runs.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        if (mv is null) errors["mvDesignRunId"] = ["Run the MV design first; the study is made on a finished MV design."];
        else if (cp is not null && SupplyOf(mv) is { } s && (Math.Abs(s[0] - cp.Location.X) > SameSupply || Math.Abs(s[1] - cp.Location.Y) > SameSupply))
            errors["mvDesignRunId"] = ["The MV design was fed from a different point than the connection point; run the MV design again."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var parameters = new BulkStudyParameters(mv!.Id);
        var run = new DesignRun(Guid.CreateVersion7(), projectId, DesignKinds.Bulk, JsonSerializer.Serialize(parameters, JsonSerializerOptions.Web), mv.RulesRef, userId, time.GetUtcNow());
        db.DesignRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var job = await queue.EnqueueAsync(BulkStudyJob.JobKind, new { designRunId = run.Id }, userId, projectId, ct);
        run.Queue(job.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted($"/api/projects/{projectId}/bulk-studies/{run.Id}", new StartedDesign(DesignEndpoints.ToDto(run), JobDto.From(job)));
    }

    private static double[]? SupplyOf(DesignRun mv)
    {
        if (mv.InputJson is null) return null;
        using var doc = JsonDocument.Parse(mv.InputJson);
        return doc.RootElement.TryGetProperty("supply", out var s) && s.ValueKind == JsonValueKind.Array && s.GetArrayLength() == 2
            ? [s[0].GetDouble(), s[1].GetDouble()]
            : null;
    }
}
