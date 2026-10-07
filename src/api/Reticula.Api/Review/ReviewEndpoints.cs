using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Reticula.Api.Design;
using Reticula.Api.Infrastructure;
using Reticula.Api.Jobs;
using Reticula.Domain.Auth;
using Reticula.Domain.Design;
using Reticula.Domain.Field;
using Reticula.Domain.Review;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;
using Reticula.Infrastructure.Documents;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Jobs;
using Reticula.Infrastructure.Review;

namespace Reticula.Api.Review;

public sealed record ConfirmAssumptionRequest(string Note);

public sealed record RevisionDto(Guid Id, int Number, string Label, string? Description, Guid DesignRunId, int DesignRunNumber, string RulesRef,
    string RulesHash, string InputsHash, string ResultHash, bool FitToSubmit, DateTimeOffset CreatedAt, DateTimeOffset? SignedOffAt, string? EngineerName,
    string? RegistrationNo, string? SignOffStatement, bool? Reproduced, DateTimeOffset? ReproducedAt, bool Locked);

/// <param name="Blockers">What stops sign-off; empty when the revision can be signed.</param>
public sealed record RevisionDetail(RevisionDto Revision, IReadOnlyList<string> Blockers);

public sealed record CreateRevisionRequest(Guid? DesignRunId, string? Label, string? Description);

public sealed record SignOffRequest(string? Statement);

public sealed record SignOffAccepted(RevisionDto Revision, JobDto Documents);

public sealed record AuditEntryDto(Guid Id, DateTimeOffset At, Guid? UserId, string? UserName, string EntityType, string EntityId, string Action,
    JsonElement? Before, JsonElement? After);

public sealed record ReportSectionDto(Guid Id, string Key, string Title, string Text, string Source, string Status, int Order, DateTimeOffset UpdatedAt,
    DateTimeOffset? ApprovedAt, uint Version);

public sealed record SaveReportSectionRequest(string Title, string Text, int? Order, uint? Version);

/// <summary>
/// Review (Phase 7): assumptions confirmed by the engineer (7.1), numbered revisions that reproduce (7.2), sign-off by the registered
/// engineer (7.3), the audit trail (7.4), the project export (7.5) and the report's text sections (8.4).
/// </summary>
public static partial class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{projectId:guid}").WithTags("Review").RequireAuthorization(Policies.Engineer);
        g.MapPost("/assumptions/{assumptionId:guid}/confirm", ConfirmAssumption);
        g.MapGet("/revisions", ListRevisions);
        g.MapPost("/revisions", CreateRevision);
        g.MapGet("/revisions/{revisionId:guid}", GetRevision);
        g.MapPost("/revisions/{revisionId:guid}/reproduce", Reproduce);
        g.MapPost("/revisions/{revisionId:guid}/documents", RevisionDocuments);
        g.MapPost("/revisions/{revisionId:guid}/sign-off", SignOff);
        g.MapGet("/audit", Audit);
        g.MapGet("/export", Export);
        g.MapGet("/report-sections", ListSections);
        g.MapPut("/report-sections/{key}", SaveSection);
        g.MapPost("/report-sections/{key}/approve", ApproveSection);
        g.MapDelete("/report-sections/{key}", DeleteSection);
        return app;
    }

    // ---------- assumptions (7.1) ----------

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> ConfirmAssumption(Guid projectId, Guid assumptionId, ConfirmAssumptionRequest req,
        ReticulaDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var a = await db.Assumptions.FirstOrDefaultAsync(x => x.Id == assumptionId && x.ProjectId == projectId, ct);
        if (a is null) return TypedResults.NotFound();
        if (string.IsNullOrWhiteSpace(req.Note) || req.Note.Length > 1000)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["note"] = ["Say why the assumption is acceptable (at most 1000 characters)."] });
        a.Confirm(user.UserId(), req.Note.Trim(), time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---------- revisions (7.2) and sign-off (7.3) ----------

    private static async Task<Results<Ok<List<RevisionDto>>, NotFound>> ListRevisions(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        if (!await db.Projects.AnyActiveAsync(projectId, ct)) return TypedResults.NotFound();
        var runs = await RunNumbersAsync(db, projectId, ct);
        var revs = await db.Revisions.AsNoTracking().Where(r => r.ProjectId == projectId).OrderByDescending(r => r.Number).ToListAsync(ct);
        return TypedResults.Ok(revs.Select(r => ToDto(r, runs)).ToList());
    }

    private static async Task<Results<Created<RevisionDto>, ValidationProblem, NotFound>> CreateRevision(Guid projectId, CreateRevisionRequest req,
        ReticulaDbContext db, ReviewService review, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await db.Projects.ActiveAsync(projectId, ct);
        if (project is null) return TypedResults.NotFound();
        if (req.Label is { Length: > 100 } || req.Description is { Length: > 2000 })
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["label"] = ["Label at most 100 characters, description at most 2000."] });
        var (rev, problem) = await review.CreateAsync(project, req.DesignRunId, req.Label, req.Description, user.UserId(), ct);
        if (rev is null) return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["designRunId"] = [problem!] });
        return TypedResults.Created($"/api/projects/{projectId}/revisions/{rev.Id}", ToDto(rev, await RunNumbersAsync(db, projectId, ct)));
    }

    private static async Task<Results<Ok<RevisionDetail>, NotFound>> GetRevision(Guid projectId, Guid revisionId, ReticulaDbContext db, ReviewService review,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await db.Projects.ActiveAsync(projectId, ct);
        var rev = project is null ? null : await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == revisionId && r.ProjectId == projectId, ct);
        if (rev is null) return TypedResults.NotFound();
        var registration = await db.Users.Where(u => u.Id == user.UserId()).Select(u => u.RegistrationNo).FirstOrDefaultAsync(ct);
        var blockers = rev.Locked ? [] : await review.SignOffBlockersAsync(project!, rev, registration, ct);
        return TypedResults.Ok(new RevisionDetail(ToDto(rev, await RunNumbersAsync(db, projectId, ct)), blockers));
    }

    private static async Task<Results<Accepted<JobDto>, NotFound, Conflict<string>>> Reproduce(Guid projectId, Guid revisionId, ReticulaDbContext db,
        IJobQueue queue, ClaimsPrincipal user, CancellationToken ct)
    {
        var rev = await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == revisionId && r.ProjectId == projectId, ct);
        if (rev is null || !await db.Projects.AnyActiveAsync(projectId, ct)) return TypedResults.NotFound();
        if (await DesignEndpoints.LatestJobAsync(db, projectId, DesignRunJob.JobKind, ct) is { IsFinished: false })
            return TypedResults.Conflict("A design job is running; reproduce the revision when it has finished.");
        var job = await queue.EnqueueAsync(DesignRunJob.JobKind, new DesignRunPayload(DesignRunModes.Reproduce, ParentRunId: rev.DesignRunId, RevisionId: rev.Id),
            user.UserId(), projectId, ct);
        return TypedResults.Accepted($"/api/jobs/{job.Id}", JobDto.From(job));
    }

    private static async Task<Results<Accepted<JobDto>, NotFound, Conflict<string>>> RevisionDocuments(Guid projectId, Guid revisionId, ReticulaDbContext db,
        IJobQueue queue, ClaimsPrincipal user, CancellationToken ct)
    {
        var rev = await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == revisionId && r.ProjectId == projectId, ct);
        if (rev is null || !await db.Projects.AnyActiveAsync(projectId, ct)) return TypedResults.NotFound();
        if (rev.Locked) return TypedResults.Conflict($"Revision {rev.Number} is signed off; its documents are locked.");
        var job = await queue.EnqueueAsync(DocumentJob.JobKind, new DocumentPayload(rev.DesignRunId, rev.Id), user.UserId(), projectId, ct);
        return TypedResults.Accepted($"/api/jobs/{job.Id}", JobDto.From(job));
    }

    private static async Task<Results<Accepted<SignOffAccepted>, NotFound, ProblemHttpResult>> SignOff(Guid projectId, Guid revisionId, SignOffRequest? req,
        ReticulaDbContext db, ReviewService review, IJobQueue queue, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await db.Projects.ActiveAsync(projectId, ct);
        var rev = project is null ? null : await db.Revisions.FirstOrDefaultAsync(r => r.Id == revisionId && r.ProjectId == projectId, ct);
        if (rev is null) return TypedResults.NotFound();
        var engineer = await db.Users.AsNoTracking().Where(u => u.Id == user.UserId()).Select(u => new { u.DisplayName, u.RegistrationNo }).FirstAsync(ct);
        var blockers = await review.SignOffBlockersAsync(project!, rev, engineer.RegistrationNo, ct);
        if (blockers.Count > 0)
            return TypedResults.Problem(title: $"Revision {rev.Number} cannot be signed off yet", statusCode: StatusCodes.Status409Conflict,
                detail: string.Join(" ", blockers), extensions: new Dictionary<string, object?> { ["reasons"] = blockers });
        var statement = string.IsNullOrWhiteSpace(req?.Statement) ? ReviewService.DefaultStatement : req.Statement.Trim();
        if (statement.Length > 2000) statement = statement[..2000];
        rev.SignOff(user.UserId(), engineer.DisplayName, engineer.RegistrationNo!, statement, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        // The documents are made again with the sign-off on their stamp, then locked.
        var job = await queue.EnqueueAsync(DocumentJob.JobKind, new DocumentPayload(rev.DesignRunId, rev.Id), user.UserId(), projectId, ct);
        return TypedResults.Accepted($"/api/jobs/{job.Id}", new SignOffAccepted(ToDto(rev, await RunNumbersAsync(db, projectId, ct)), JobDto.From(job)));
    }

    private static RevisionDto ToDto(Revision r, IReadOnlyDictionary<Guid, int> runs) => new(r.Id, r.Number, r.Label, r.Description, r.DesignRunId,
        runs.GetValueOrDefault(r.DesignRunId), r.RulesRef, r.RulesHash, r.InputsHash, r.ResultHash, r.FitToSubmit, r.CreatedAt, r.SignedOffAt, r.EngineerName,
        r.RegistrationNo, r.SignOffStatement, r.Reproduced, r.ReproducedAt, r.Locked);

    private static Task<Dictionary<Guid, int>> RunNumbersAsync(ReticulaDbContext db, Guid projectId, CancellationToken ct) =>
        db.DesignRuns.AsNoTracking().Where(r => r.ProjectId == projectId).ToDictionaryAsync(r => r.Id, r => r.Number, ct);

    // ---------- audit (7.4) and export (7.5) ----------

    private static async Task<Results<Ok<List<AuditEntryDto>>, NotFound>> Audit(Guid projectId, DateTimeOffset? before, int? take, string? entityType,
        ReticulaDbContext db, CancellationToken ct)
    {
        if (!await db.Projects.AnyActiveAsync(projectId, ct)) return TypedResults.NotFound();
        var q = db.AuditEntries.AsNoTracking().Where(a => a.ProjectId == projectId);
        if (before is { } b) q = q.Where(a => a.At < b);
        if (!string.IsNullOrWhiteSpace(entityType)) q = q.Where(a => a.EntityType == entityType);
        var rows = await q.OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Take(Math.Clamp(take ?? 100, 1, 500)).ToListAsync(ct);
        var ids = rows.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        static JsonElement? J(string? s) => s is null ? null : JsonDocument.Parse(s).RootElement.Clone();
        return TypedResults.Ok(rows.Select(a => new AuditEntryDto(a.Id, a.At, a.UserId, a.UserId is { } u ? names.GetValueOrDefault(u) : null, a.EntityType,
            a.EntityId, a.Action, J(a.BeforeJson), J(a.AfterJson))).ToList());
    }

    private static async Task<Results<FileStreamHttpResult, NotFound>> Export(Guid projectId, ProjectExport export, CancellationToken ct)
    {
        var file = await export.WriteAsync(projectId, ct);
        if (file is null) return TypedResults.NotFound();
        return TypedResults.Stream(file.Value.Stream, "application/zip", file.Value.FileName);
    }

    // ---------- report sections (8.4) ----------

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,49}$")]
    private static partial Regex SectionKey();

    private static async Task<Results<Ok<List<ReportSectionDto>>, NotFound>> ListSections(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        if (!await db.Projects.AnyActiveAsync(projectId, ct)) return TypedResults.NotFound();
        var rows = await db.ReportSections.AsNoTracking().Where(s => s.ProjectId == projectId).OrderBy(s => s.Order).ThenBy(s => s.Key).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<ReportSectionDto>, ValidationProblem, NotFound, Conflict>> SaveSection(Guid projectId, string key,
        SaveReportSectionRequest req, ReticulaDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await db.Projects.AnyActiveAsync(projectId, ct)) return TypedResults.NotFound();
        var errors = new Dictionary<string, string[]>();
        if (!SectionKey().IsMatch(key)) errors["key"] = ["Lower-case letters, digits and hyphens, at most 50."];
        if (string.IsNullOrWhiteSpace(req.Title) || req.Title.Length > 200) errors["title"] = ["A title of at most 200 characters."];
        if (req.Text is null || req.Text.Length > 20000) errors["text"] = ["At most 20 000 characters."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
        var now = time.GetUtcNow();
        var s = await db.ReportSections.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Key == key, ct);
        if (s is null)
        {
            s = new ReportSection(Guid.CreateVersion7(), projectId, key, req.Title.Trim(), req.Text!, "engineer", user.UserId(), now);
            s.Edit(s.Title, s.Text, "engineer", req.Order ?? 0, user.UserId(), now);
            db.ReportSections.Add(s);
        }
        else
        {
            if (req.Version is { } v && v != s.Version) return TypedResults.Conflict();
            s.Edit(req.Title.Trim(), req.Text!, "engineer", req.Order ?? s.Order, user.UserId(), now);
        }
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(s));
    }

    private static async Task<Results<Ok<ReportSectionDto>, NotFound>> ApproveSection(Guid projectId, string key, ReticulaDbContext db, TimeProvider time,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var s = await db.ReportSections.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Key == key, ct);
        if (s is null) return TypedResults.NotFound();
        s.Approve(user.UserId(), time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(s));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteSection(Guid projectId, string key, ReticulaDbContext db, CancellationToken ct)
    {
        var s = await db.ReportSections.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Key == key, ct);
        if (s is null) return TypedResults.NotFound();
        db.ReportSections.Remove(s);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static ReportSectionDto ToDto(ReportSection s) => new(s.Id, s.Key, s.Title, s.Text, s.Source, s.Status, s.Order, s.UpdatedAt, s.ApprovedAt, s.Version);
}
