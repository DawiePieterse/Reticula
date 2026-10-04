using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Auth;
using Reticula.Domain.Layout;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Geo;
using Reticula.Infrastructure.Layout;

namespace Reticula.Api.Layout;

/// <summary>GeoJSON feature. Geometry is a PolygonDto, PointDto or LineStringDto, serialised by its runtime type.</summary>
public sealed record GeoFeature<TProps>(string Type, string Id, object Geometry, TProps Properties);

public sealed record GeoFeatureCollection<TProps>(string Type, IReadOnlyList<GeoFeature<TProps>> Features)
{
    public static GeoFeatureCollection<TProps> Of(IEnumerable<GeoFeature<TProps>> features) => new("FeatureCollection", [.. features]);
}

public sealed record StandProps(string? Erf, string? Zoning, double AreaM2);

public sealed record BuildingProps(
    string PredictedType, double Confidence, string Source, bool LowConfidence, string Status, string? ConfirmedType,
    string EffectiveType, double AreaM2, string? Erf, string? Zoning, JsonElement Signals, uint Version);

public sealed record PreviewProps(string Ref, string? Erf, double AreaM2);

public sealed record ImportResponse(
    Guid? BatchId, bool Committed, string Format, string? SourceCrs, string CrsReason, int FeatureCount,
    IReadOnlyList<CalcIssue> Issues, IReadOnlyList<CalcLayer> Layers, GeoFeatureCollection<PreviewProps>? Preview);

public sealed record ImportBatchDto(Guid Id, string Kind, string FileName, string Format, string? SourceCrs, int FeatureCount,
    JsonElement Issues, DateTimeOffset CreatedAt);

public sealed record LayoutSummary(int Stands, int StandsWithoutErf, int Buildings, int LowConfidence, int Inspected,
    IReadOnlyDictionary<string, int> PredictedByType);

public static class LayoutEndpoints
{
    private const long MaxUploadBytes = 50L * 1024 * 1024;

    public static IEndpointRouteBuilder MapLayoutEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{projectId:guid}").WithTags("Layout").RequireAuthorization(Policies.FieldUser);
        g.MapPost("/imports", Import)
            .RequireAuthorization(Policies.Engineer)
            .DisableAntiforgery() // bearer tokens only, no cookies
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes + 1024 * 1024));
        g.MapGet("/imports", ListImports);
        g.MapGet("/stands", Stands);
        g.MapGet("/buildings", Buildings);
        g.MapGet("/layout-summary", Summary);
        g.MapPost("/predictions", Repredict).RequireAuthorization(Policies.Engineer);
        return app;
    }

    private static async Task<Results<Ok<ImportResponse>, Created<ImportResponse>, NotFound, ValidationProblem, BadRequest<ImportResponse>>> Import(
        Guid projectId, IFormFile file, [FromForm] string kind, [FromForm] string? sourceCrs, [FromForm] string? layer, [FromForm] bool? dryRun,
        ReticulaDbContext db, LayoutService layout, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.ArchivedAt == null, ct);
        if (project is null) return TypedResults.NotFound();

        var errors = new Dictionary<string, string[]>();
        if (!ImportKinds.All.Contains(kind)) errors["kind"] = [$"Kind must be one of: {string.Join(", ", ImportKinds.All)}."];
        if (file.Length == 0) errors["file"] = ["The file is empty."];
        if (file.Length > MaxUploadBytes) errors["file"] = ["The file is larger than 50 MB."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        byte[] data;
        await using (var s = file.OpenReadStream())
        using (var ms = new MemoryStream((int)file.Length))
        {
            await s.CopyToAsync(ms, ct);
            data = ms.ToArray();
        }

        ImportOutcome outcome;
        try
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            outcome = await layout.ImportAsync(project, kind, file.FileName, data, sourceCrs, layer, dryRun ?? false, userId, ct);
        }
        catch (CalcRejectedException e)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [e.Message] });
        }

        var r = outcome.Result;
        var preview = dryRun == true
            ? GeoFeatureCollection<PreviewProps>.Of(r.Features.Select(f => new GeoFeature<PreviewProps>("Feature", f.Ref, f.Geometry, new PreviewProps(f.Ref, f.Erf, f.AreaM2))))
            : null;
        var response = new ImportResponse(outcome.Batch?.Id, outcome.Batch is not null, r.Format, r.SourceCrs, r.CrsReason, r.Features.Count, r.Issues, r.Layers, preview);

        if (dryRun == true) return TypedResults.Ok(response);
        if (outcome.Batch is null) return TypedResults.BadRequest(response); // file has errors; nothing stored
        return TypedResults.Created($"/api/projects/{projectId}/imports", response);
    }

    private static async Task<Ok<List<ImportBatchDto>>> ListImports(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        var batches = await db.ImportBatches.AsNoTracking().Where(b => b.ProjectId == projectId).OrderByDescending(b => b.CreatedAt).ToListAsync(ct);
        return TypedResults.Ok(batches.Select(b => new ImportBatchDto(b.Id, b.Kind, b.FileName, b.Format, b.SourceCrs, b.FeatureCount,
            JsonDocument.Parse(b.IssuesJson).RootElement.Clone(), b.CreatedAt)).ToList());
    }

    private static async Task<Ok<GeoFeatureCollection<StandProps>>> Stands(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        var stands = await db.Stands.AsNoTracking().Where(s => s.ProjectId == projectId).OrderBy(s => s.ErfNumber).ToListAsync(ct);
        return TypedResults.Ok(GeoFeatureCollection<StandProps>.Of(stands.Select(s =>
            new GeoFeature<StandProps>("Feature", s.Id.ToString(), PolygonDto.From(s.Geometry), new StandProps(s.ErfNumber, s.Zoning, s.AreaM2)))));
    }

    private static async Task<Ok<GeoFeatureCollection<BuildingProps>>> Buildings(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        var rows = await (
            from b in db.Buildings.AsNoTracking()
            where b.ProjectId == projectId
            join s in db.Stands.AsNoTracking() on b.StandId equals s.Id into stands
            from s in stands.DefaultIfEmpty()
            orderby b.LowConfidence descending, b.PredictedConfidence
            select new { b, Erf = s == null ? null : s.ErfNumber }).ToListAsync(ct);

        return TypedResults.Ok(GeoFeatureCollection<BuildingProps>.Of(rows.Select(x => new GeoFeature<BuildingProps>(
            "Feature", x.b.Id.ToString(), x.b.Footprint is null ? PointDto.From(x.b.Location) : PolygonDto.From(x.b.Footprint),
            new BuildingProps(x.b.PredictedType, x.b.PredictedConfidence, x.b.PredictionSource, x.b.LowConfidence,
                x.b.Status.ToString().ToLowerInvariant(), x.b.ConfirmedType, x.b.EffectiveType, x.b.AreaM2, x.Erf, x.b.Zoning,
                JsonDocument.Parse(x.b.PredictionSignalsJson).RootElement.Clone(), x.b.Version)))));
    }

    private static async Task<Results<Ok<LayoutSummary>, NotFound>> Summary(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)) return TypedResults.NotFound();
        var stands = await db.Stands.CountAsync(s => s.ProjectId == projectId, ct);
        var withoutErf = await db.Stands.CountAsync(s => s.ProjectId == projectId && s.ErfNumber == null, ct);
        var buildings = db.Buildings.Where(b => b.ProjectId == projectId);
        var byType = await buildings.Where(b => b.Status == BuildingStatus.Predicted)
            .GroupBy(b => b.PredictedType).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return TypedResults.Ok(new LayoutSummary(
            stands, withoutErf,
            await buildings.CountAsync(ct),
            await buildings.CountAsync(b => b.Status == BuildingStatus.Predicted && b.LowConfidence, ct),
            await buildings.CountAsync(b => b.Status != BuildingStatus.Predicted, ct),
            byType));
    }

    private static async Task<Results<NoContent, NotFound>> Repredict(Guid projectId, ReticulaDbContext db, LayoutService layout, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.ArchivedAt == null, ct);
        if (project is null) return TypedResults.NotFound();
        await layout.RefreshBuildingsAsync(project, ct);
        return TypedResults.NoContent();
    }
}
