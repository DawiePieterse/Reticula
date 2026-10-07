using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Reticula.Api.Design;
using Reticula.Api.Infrastructure;
using Reticula.Api.Jobs;
using Reticula.Domain.Auth;
using Reticula.Domain.Documents;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;
using Reticula.Infrastructure.Documents;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Api.Documents;

/// <param name="Stale">Why the document no longer shows the project's design, or null.</param>
public sealed record DocumentDto(Guid Id, string Kind, string Title, string Number, string FileName, string ContentType, long SizeBytes, string Sha256,
    Guid DesignRunId, int DesignRunNumber, Guid? RevisionId, int RevisionNumber, string RulesRef, string RulesHash, string RateDate, DateOnly DesignDate,
    DateTimeOffset CreatedAt, bool Locked, bool Superseded, string? Stale);

public sealed record DocumentsStatus(IReadOnlyList<DocumentDto> Documents, JobDto? Job);

public sealed record GenerateDocumentsRequest(Guid? DesignRunId, Guid? RevisionId);

/// <summary>Design documents (Phase 6): generate all from a design run, list them with what they were made from, download.</summary>
public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{projectId:guid}/documents").WithTags("Documents").RequireAuthorization(Policies.Engineer);
        g.MapGet("/", List);
        g.MapPost("/", Generate);
        g.MapGet("/{documentId:guid}/file", Download);
        return app;
    }

    private static async Task<Results<Ok<DocumentsStatus>, NotFound>> List(Guid projectId, bool? all, ReticulaDbContext db, DesignInputsBuilder inputs,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await db.Projects.ActiveAsync(projectId, ct);
        if (project is null) return TypedResults.NotFound();
        var docs = await db.Documents.AsNoTracking().Where(d => d.ProjectId == projectId && (all == true || d.SupersededAt == null))
            .OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Number).ToListAsync(ct);
        var runNumbers = await db.DesignRuns.AsNoTracking().Where(r => r.ProjectId == projectId).ToDictionaryAsync(r => r.Id, r => r.Number, ct);
        var current = (await DesignRuns.CurrentAsync(db, projectId, ct))?.Id;
        var hash = docs.Count == 0 ? null : (await inputs.BuildAsync(project, user.UserId(), ct)).Hash;
        string? Stale(Document d) => d.Locked ? null
            : d.DesignRunId != current ? "A newer design run replaced the one this document shows."
            : d.InputsHash != hash ? "The project's inputs changed after the design run this document shows."
            : null;
        var job = await DesignEndpoints.LatestJobAsync(db, projectId, DocumentJob.JobKind, ct);
        if (job is { IsFinished: true } && docs.FirstOrDefault() is { } latest && job.CreatedAt <= latest.CreatedAt) job = null;
        return TypedResults.Ok(new DocumentsStatus([.. docs.Select(d => new DocumentDto(d.Id, d.Kind, d.Title, d.Number, d.FileName, d.ContentType, d.SizeBytes,
            d.Sha256, d.DesignRunId, runNumbers.GetValueOrDefault(d.DesignRunId), d.RevisionId, d.RevisionNumber, d.RulesRef, d.RulesHash, d.RateDate,
            d.DesignDate, d.CreatedAt, d.Locked, d.SupersededAt is not null, Stale(d)))], job is null ? null : JobDto.From(job)));
    }

    private static async Task<Results<Accepted<JobDto>, ValidationProblem, NotFound>> Generate(Guid projectId, GenerateDocumentsRequest? req,
        ReticulaDbContext db, IJobQueue queue, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await db.Projects.AnyActiveAsync(projectId, ct)) return TypedResults.NotFound();
        if (req?.RevisionId is { } rid && !await db.Revisions.AnyAsync(r => r.Id == rid && r.ProjectId == projectId, ct))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["revisionId"] = ["No such revision."] });
        var hasDesign = req?.DesignRunId is { } id
            ? await db.DesignRuns.AnyAsync(r => r.Id == id && r.ProjectId == projectId && r.ResultJson != null && r.Mode != "optimise", ct)
            : req?.RevisionId is not null || await DesignRuns.CurrentAsync(db, projectId, ct) is not null;
        if (!hasDesign) return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["designRunId"] = ["Run the design first."] });
        var running = await DesignEndpoints.LatestJobAsync(db, projectId, DocumentJob.JobKind, ct);
        var job = running is { IsFinished: false }
            ? running
            : await queue.EnqueueAsync(DocumentJob.JobKind, new DocumentPayload(req?.DesignRunId, req?.RevisionId), user.UserId(), projectId, ct);
        return TypedResults.Accepted($"/api/jobs/{job.Id}", JobDto.From(job));
    }

    private static async Task<Results<FileStreamHttpResult, NotFound>> Download(Guid projectId, Guid documentId, ReticulaDbContext db, IFileStore files,
        CancellationToken ct)
    {
        if (!await db.Projects.AnyActiveAsync(projectId, ct)) return TypedResults.NotFound();
        var d = await db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == documentId && x.ProjectId == projectId, ct);
        if (d is null || await files.OpenReadAsync(d.StorageKey, ct) is not { } stream) return TypedResults.NotFound();
        return TypedResults.Stream(stream, d.ContentType, d.FileName);
    }
}
