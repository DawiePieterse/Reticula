using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Reticula.Api.Geo;
using Reticula.Domain.Auth;
using Reticula.Domain.Projects;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;

namespace Reticula.Api.Projects;

public sealed record ProjectDto(
    Guid Id, string Name, string RulesRef, string Authority, PolygonDto Area,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version)
{
    public static ProjectDto From(Project p) =>
        new(p.Id, p.Name, p.RulesRef, p.Authority, PolygonDto.From(p.Area), p.CreatedAt, p.UpdatedAt, p.Version);
}

/// <param name="Version">Required on update: the version the client last read. A mismatch returns 409.</param>
public sealed record SaveProjectRequest(string Name, string RulesRef, PolygonDto? Area, uint? Version);

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects").WithTags("Projects").RequireAuthorization(Policies.FieldUser);
        g.MapGet("/", List);
        g.MapGet("/{id:guid}", Get);
        g.MapPost("/", Create).RequireAuthorization(Policies.Engineer);
        g.MapPut("/{id:guid}", Update).RequireAuthorization(Policies.Engineer);
        g.MapDelete("/{id:guid}", Archive).RequireAuthorization(Policies.Engineer);
        return app;
    }

    private static async Task<Ok<List<ProjectDto>>> List(ReticulaDbContext db, CancellationToken ct)
    {
        var projects = await db.Projects.AsNoTracking()
            .Where(p => p.ArchivedAt == null)
            .OrderByDescending(p => p.UpdatedAt)
            .ToListAsync(ct);
        return TypedResults.Ok(projects.Select(ProjectDto.From).ToList());
    }

    private static async Task<Results<Ok<ProjectDto>, NotFound>> Get(Guid id, ReticulaDbContext db, CancellationToken ct)
    {
        var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        return p is null ? TypedResults.NotFound() : TypedResults.Ok(ProjectDto.From(p));
    }

    private static async Task<Results<Created<ProjectDto>, ValidationProblem>> Create(
        SaveProjectRequest req, ReticulaDbContext db, ICalcClient calc, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var (area, errors) = await ValidateAsync(req, calc, ct);
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var project = new Project(Guid.CreateVersion7(), req.Name.Trim(), req.RulesRef, area!, userId, time.GetUtcNow());
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/projects/{project.Id}", ProjectDto.From(project));
    }

    private static async Task<Results<Ok<ProjectDto>, NotFound, ValidationProblem, Conflict<ProblemDetailsBody>>> Update(
        Guid id, SaveProjectRequest req, ReticulaDbContext db, ICalcClient calc, TimeProvider time, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (project is null) return TypedResults.NotFound();

        var (area, errors) = await ValidateAsync(req, calc, ct);
        if (req.Version is null) errors["version"] = ["Version is required when updating."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        db.Entry(project).Property(p => p.Version).OriginalValue = req.Version!.Value;
        project.Update(req.Name.Trim(), req.RulesRef, area!, time.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return TypedResults.Conflict(ProblemDetailsBody.Conflict);
        }
        return TypedResults.Ok(ProjectDto.From(project));
    }

    private static async Task<Results<NoContent, NotFound>> Archive(Guid id, ReticulaDbContext db, TimeProvider time, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct);
        if (project is null) return TypedResults.NotFound();
        project.Archive(time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<(Polygon? Area, Dictionary<string, string[]> Errors)> ValidateAsync(
        SaveProjectRequest req, ICalcClient calc, CancellationToken ct)
    {
        Polygon? area = null;
        string? areaError = null;
        if (req.Area is not null && !req.Area.TryToPolygon(out area, out areaError)) area = null;

        var errors = new Dictionary<string, string[]>(ProjectRules.Validate(req.Name, req.RulesRef, area));
        if (areaError is not null) errors["area"] = [areaError];

        // Rules must exist in the calc service; it is the single owner of rules files.
        if (!errors.ContainsKey("rulesRef") && await calc.GetRulesInfoAsync(req.RulesRef, ct) is null)
            errors["rulesRef"] = [$"Rules file {req.RulesRef} does not exist."];

        return (area, errors);
    }
}

public sealed record ProblemDetailsBody(string Title, string Detail, int Status)
{
    public static readonly ProblemDetailsBody Conflict = new(
        "Changed elsewhere",
        "This project was changed by someone else since you opened it. Reload to see their version; your changes were not saved.",
        StatusCodes.Status409Conflict);
}
