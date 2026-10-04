using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Reticula.Api.Jobs;
using Reticula.Domain.Auth;
using Reticula.Domain.Design;
using Reticula.Domain.Documents;
using Reticula.Domain.Field;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Documents;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Review;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Api.Documents;

public sealed record GenerateDocumentsRequest(string? Engineer);

public sealed record DocumentDto(Guid Id, string Kind, string FileName, string Title, string ContentType, long SizeBytes, string Sha256, DateTimeOffset CreatedAt);

public sealed record DocumentSetDto(Guid Id, int Number, string Revision, bool Locked, string? SignedOff, string Status, Guid? JobId, string RulesRef, string? Engineer, JsonElement? Checklist,
    JsonElement? Warnings, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, IReadOnlyList<DocumentDto> Documents);

/// <summary>The latest set is stale when anything upstream changed since it was made (plan 6.7).</summary>
public sealed record DocumentSetsIndex(IReadOnlyList<DocumentSetDto> Sets, bool Stale, IReadOnlyList<string> Changes);

public sealed record StartedDocuments(DocumentSetDto Set, JobDto Job);

public sealed record DocumentLink(string Url, DateTimeOffset ExpiresAt);

/// <summary>Design documents (plan Phase 6): generate all, list with stale detection, signed downloads.</summary>
public static class DocumentEndpoints
{
    private const string Purpose = "Reticula.DocumentLinks.v1";
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(10);

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{projectId:guid}").WithTags("Documents").RequireAuthorization(Policies.FieldUser);
        g.MapPost("/document-sets", Generate).RequireAuthorization(Policies.Engineer);
        g.MapGet("/document-sets", List);
        g.MapGet("/document-sets/{setId:guid}", Get);
        g.MapGet("/documents/{documentId:guid}/link", Link);
        g.MapGet("/load-schedule.xlsx", LoadScheduleXlsx);
        // Signed links work without the bearer token (a browser download), for a short time only.
        app.MapGet("/api/document-files/{documentId:guid}", Download).WithTags("Documents").AllowAnonymous();
        return app;
    }

    private static async Task<Results<Accepted<StartedDocuments>, NotFound, ValidationProblem>> Generate(Guid projectId, GenerateDocumentsRequest req, ReticulaDbContext db,
        AssumptionRegister register, IJobQueue queue, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.ArchivedAt == null, ct);
        if (project is null) return TypedResults.NotFound();
        if (req.Engineer is { Length: > 200 }) return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["engineer"] = ["At most 200 characters."] });
        // The register is brought up to date first: the documents list it and its state is part of their sources.
        await register.SyncAsync(projectId, ct);
        var sources = await DocumentSourceSet.CurrentAsync(db, projectId, ct);
        if (sources.Lv.Count == 0)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["lvDesign"] = ["Run an LV design before generating documents."] });
        var number = (await db.DocumentSets.Where(s => s.ProjectId == projectId).MaxAsync(s => (int?)s.Number, ct) ?? 0) + 1;
        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var engineer = string.IsNullOrWhiteSpace(req.Engineer) ? user.FindFirstValue(ClaimTypes.Name) : req.Engineer.Trim();
        var set = new DocumentSet(Guid.CreateVersion7(), projectId, number, sources.ToJson(), sources.Hash(), project.RulesRef, engineer, userId, time.GetUtcNow());
        db.DocumentSets.Add(set);
        await db.SaveChangesAsync(ct);
        var job = await queue.EnqueueAsync(DocumentsJob.JobKind, new { documentSetId = set.Id }, userId, projectId, ct);
        set.Queue(job.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted($"/api/projects/{projectId}/document-sets/{set.Id}", new StartedDocuments(ToDto(set, []), JobDto.From(job)));
    }

    private static async Task<Results<Ok<DocumentSetsIndex>, NotFound>> List(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)) return TypedResults.NotFound();
        var sets = await db.DocumentSets.AsNoTracking().Where(s => s.ProjectId == projectId).OrderByDescending(s => s.Number).Take(30).ToListAsync(ct);
        var ids = sets.Select(s => s.Id).ToList();
        var docs = (await db.ProjectDocuments.AsNoTracking().Where(d => ids.Contains(d.SetId)).OrderBy(d => d.CreatedAt).ToListAsync(ct)).ToLookup(d => d.SetId);
        var latest = sets.FirstOrDefault(s => s.Status == DocumentSetStatus.Succeeded);
        var stale = false;
        IReadOnlyList<string> changes = [];
        if (latest is not null)
        {
            var current = await DocumentSourceSet.CurrentAsync(db, projectId, ct);
            stale = current.Hash() != latest.SourcesHash;
            if (stale && DocumentSourceSet.FromJson(latest.SourcesJson) is { } then) changes = current.ChangesFrom(then);
        }
        return TypedResults.Ok(new DocumentSetsIndex([.. sets.Select(s => ToDto(s, docs[s.Id]))], stale, changes));
    }

    private static async Task<Results<Ok<DocumentSetDto>, NotFound>> Get(Guid projectId, Guid setId, ReticulaDbContext db, CancellationToken ct)
    {
        var set = await db.DocumentSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == setId && s.ProjectId == projectId, ct);
        if (set is null) return TypedResults.NotFound();
        var docs = await db.ProjectDocuments.AsNoTracking().Where(d => d.SetId == setId).OrderBy(d => d.CreatedAt).ToListAsync(ct);
        return TypedResults.Ok(ToDto(set, docs));
    }

    private static async Task<Results<Ok<DocumentLink>, NotFound>> Link(Guid projectId, Guid documentId, ReticulaDbContext db, IDataProtectionProvider dp,
        TimeProvider time, CancellationToken ct)
    {
        if (!await db.ProjectDocuments.AnyAsync(d => d.Id == documentId && d.ProjectId == projectId, ct)) return TypedResults.NotFound();
        var expires = time.GetUtcNow().Add(LinkLifetime);
        var token = dp.CreateProtector(Purpose).ToTimeLimitedDataProtector().Protect(documentId.ToString(), expires);
        return TypedResults.Ok(new DocumentLink($"/api/document-files/{documentId}?token={Uri.EscapeDataString(token)}", expires));
    }

    private static async Task<Results<FileStreamHttpResult, NotFound, ForbidHttpResult>> Download(Guid documentId, string? token, ReticulaDbContext db,
        IDataProtectionProvider dp, IFileStore files, CancellationToken ct)
    {
        try
        {
            if (token is null || dp.CreateProtector(Purpose).ToTimeLimitedDataProtector().Unprotect(token) != documentId.ToString())
                return TypedResults.Forbid();
        }
        catch (CryptographicException)
        {
            return TypedResults.Forbid();
        }
        var doc = await db.ProjectDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (doc is null || await files.OpenReadAsync(doc.StorageKey, ct) is not { } stream) return TypedResults.NotFound();
        return TypedResults.File(stream, doc.ContentType, doc.FileName);
    }

    /// <summary>The load schedule as a stamped spreadsheet (plan 1.10), made by the calc service like the other documents.</summary>
    private static async Task<Results<FileContentHttpResult, NotFound>> LoadScheduleXlsx(Guid projectId, ReticulaDbContext db, ICalcClient calc, TimeProvider time,
        CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.ArchivedAt == null, ct);
        if (project is null) return TypedResults.NotFound();
        var rows = await (
            from l in db.LoadPoints.AsNoTracking()
            where l.ProjectId == projectId
            join b in db.Buildings.AsNoTracking() on l.BuildingId equals b.Id
            join s in db.Stands.AsNoTracking() on b.StandId equals s.Id into st
            from s in st.DefaultIfEmpty()
            select new { l, b.Location, Erf = s == null ? null : s.ErfNumber }).ToListAsync(ct);
        var now = time.GetUtcNow();
        var package = new JsonObject
        {
            ["project"] = new JsonObject { ["id"] = project.Id.ToString(), ["name"] = project.Name, ["authority"] = project.Authority },
            ["stamp"] = new JsonObject
            {
                ["rules"] = project.RulesRef, ["rules_hash"] = rows.Select(r => r.l.RulesHash).FirstOrDefault() ?? "", ["rate_list"] = "—", ["rate_date"] = "—",
                ["design_date"] = now.ToString("yyyy-MM-dd"), ["revision"] = "working", ["generated_at"] = now.ToString("yyyy-MM-dd HH:mm 'UTC'"),
            },
            ["loads"] = new JsonArray([.. rows.Select(x => (JsonNode)new JsonObject
            {
                ["building_id"] = x.l.BuildingId.ToString(), ["erf"] = x.Erf, ["kind"] = x.l.Kind, ["load_class"] = x.l.Kind == LoadKinds.Residential ? x.l.Category : x.l.SpecialLoad,
                ["kva"] = x.l.Kva, ["phases"] = x.l.Phases, ["status"] = x.l.Status.ToString().ToLowerInvariant(), ["overridden"] = x.l.Overridden,
                ["override_reason"] = x.l.OverrideReason, ["lon"] = x.Location.X, ["lat"] = x.Location.Y,
            })]),
        };
        var result = await calc.RenderDocumentsAsync(new JsonObject { ["package"] = package, ["kinds"] = new JsonArray("loads_xlsx") }, ct);
        var file = result.GetProperty("files")[0];
        return TypedResults.File(Convert.FromBase64String(file.GetProperty("data_b64").GetString()!), file.GetProperty("content_type").GetString(),
            $"load-schedule-{projectId:N}.xlsx");
    }

    private static DocumentSetDto ToDto(DocumentSet s, IEnumerable<ProjectDocument> docs) => new(
        s.Id, s.Number, s.Revision, s.Locked, s.SignedOff, s.Status.ToString().ToLowerInvariant(), s.JobId, s.RulesRef, s.Engineer,
        s.ChecklistJson is null ? null : JsonDocument.Parse(s.ChecklistJson).RootElement.Clone(),
        s.WarningsJson is null ? null : JsonDocument.Parse(s.WarningsJson).RootElement.Clone(), s.Error, s.CreatedAt, s.FinishedAt,
        [.. docs.Select(d => new DocumentDto(d.Id, d.Kind, d.FileName, d.Title, d.ContentType, d.SizeBytes, d.Sha256, d.CreatedAt))]);
}
