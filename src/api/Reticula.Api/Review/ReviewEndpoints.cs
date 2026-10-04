using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Reticula.Api.Documents;
using Reticula.Api.Jobs;
using Reticula.Domain.Auth;
using Reticula.Domain.Documents;
using Reticula.Domain.Field;
using Reticula.Domain.Review;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Documents;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Jobs;
using Reticula.Infrastructure.Review;

namespace Reticula.Api.Review;

public sealed record RegisterRow(Guid Id, string SubjectType, Guid SubjectId, string Code, string Text, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, string? ResolvedBy, DateTimeOffset? ResolvedAt, string? Note);

public sealed record AcceptAssumptionRequest(string Note);

public sealed record RevisionDto(Guid Id, int Number, string Label, string Status, Guid DocumentSetId, string SignedOffName, string RegistrationNumber,
    DateTimeOffset SignedOffAt, string? Notes, string? Error, string? SnapshotSha256, bool? Reproduced, DateTimeOffset? ReproducedAt, JsonElement? Reproduction);

public sealed record ExportDto(Guid Id, string Status, string? FileName, long SizeBytes, string? Sha256, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

public sealed record ReviewDto(Readiness Readiness, IReadOnlyList<RevisionDto> Revisions, IReadOnlyList<ExportDto> Exports);

public sealed record SignOffRequest(string FullName, string RegistrationNumber, bool Declaration, string? Notes);

public sealed record StartedRevision(RevisionDto Revision, JobDto Job);

public sealed record StartedExport(ExportDto Export, JobDto Job);

public sealed record AuditRow(DateTimeOffset At, string? User, string EntityType, string EntityId, string Action, JsonElement Changes);

/// <summary>Review and sign-off (plan Phase 7): assumptions register, revisions, reproduction, audit trail, open export.</summary>
public static partial class ReviewEndpoints
{
    private const string ExportPurpose = "Reticula.ExportLinks.v1";
    public const string Declaration = "I have reviewed this design, its assumptions and its documents, and I take professional responsibility for it.";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 /-]{3,29}$")]
    private static partial Regex RegistrationPattern();

    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{projectId:guid}").WithTags("Review").RequireAuthorization(Policies.FieldUser);
        g.MapGet("/review", Review);
        g.MapGet("/assumption-register", Register);
        g.MapPost("/assumptions/{assumptionId:guid}/accept", Accept).RequireAuthorization(Policies.Engineer);
        g.MapPost("/assumptions/{assumptionId:guid}/reopen", Reopen).RequireAuthorization(Policies.Engineer);
        g.MapPost("/revisions", SignOff).RequireAuthorization(Policies.Engineer);
        g.MapGet("/revisions/{revisionId:guid}", GetRevision);
        g.MapPost("/revisions/{revisionId:guid}/reproduce", Reproduce).RequireAuthorization(Policies.Engineer);
        g.MapGet("/audit", Audit);
        g.MapPost("/exports", Export).RequireAuthorization(Policies.Engineer);
        g.MapGet("/exports/{exportId:guid}/link", ExportLink);
        app.MapGet("/api/export-files/{exportId:guid}", ExportDownload).WithTags("Review").AllowAnonymous();
        return app;
    }

    private static async Task<Results<Ok<ReviewDto>, NotFound>> Review(Guid projectId, ReticulaDbContext db, ReviewReadiness readiness, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)) return TypedResults.NotFound();
        var r = await readiness.CheckAsync(projectId, ct);
        var revisions = await db.Revisions.AsNoTracking().Where(x => x.ProjectId == projectId).OrderByDescending(x => x.Number).ToListAsync(ct);
        var exports = await db.ProjectExports.AsNoTracking().Where(x => x.ProjectId == projectId).OrderByDescending(x => x.CreatedAt).Take(10).ToListAsync(ct);
        return TypedResults.Ok(new ReviewDto(r, [.. revisions.Select(ToDto)], [.. exports.Select(ToDto)]));
    }

    private static async Task<Results<Ok<List<RegisterRow>>, NotFound>> Register(Guid projectId, ReticulaDbContext db, AssumptionRegister register, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)) return TypedResults.NotFound();
        await register.SyncAsync(projectId, ct);
        var rows = await (
            from a in db.Assumptions.AsNoTracking()
            where a.ProjectId == projectId
            join u in db.Users.AsNoTracking() on a.ClearedBy equals u.Id into us
            from u in us.DefaultIfEmpty()
            select new { a, Name = u == null ? null : u.DisplayName }).ToListAsync(ct);
        var order = new Dictionary<AssumptionStatus, int> { [AssumptionStatus.Open] = 0, [AssumptionStatus.Accepted] = 1, [AssumptionStatus.Cleared] = 2, [AssumptionStatus.Withdrawn] = 3 };
        return TypedResults.Ok(rows.OrderBy(x => order[x.a.Status]).ThenBy(x => x.a.SubjectType).ThenBy(x => x.a.CreatedAt)
            .Select(x => new RegisterRow(x.a.Id, x.a.SubjectType, x.a.SubjectId, x.a.Code, x.a.Text, x.a.Status.ToString().ToLowerInvariant(), x.a.CreatedAt, x.a.UpdatedAt,
                x.Name, x.a.ClearedAt, x.a.ClearNote)).ToList());
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> Accept(Guid projectId, Guid assumptionId, AcceptAssumptionRequest req, ReticulaDbContext db,
        TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var a = await db.Assumptions.FirstOrDefaultAsync(x => x.Id == assumptionId && x.ProjectId == projectId, ct);
        if (a is null) return TypedResults.NotFound();
        if (string.IsNullOrWhiteSpace(req.Note) || req.Note.Trim().Length < 5 || req.Note.Length > 1000)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["note"] = ["Say why the assumption is acceptable (5 to 1000 characters)."] });
        if (a.Status is not AssumptionStatus.Open)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Only an open assumption can be accepted."] });
        a.Accept(Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), req.Note.Trim(), time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> Reopen(Guid projectId, Guid assumptionId, ReticulaDbContext db, TimeProvider time, CancellationToken ct)
    {
        var a = await db.Assumptions.FirstOrDefaultAsync(x => x.Id == assumptionId && x.ProjectId == projectId, ct);
        if (a is null) return TypedResults.NotFound();
        a.Reopen(a.Text, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Accepted<StartedRevision>, NotFound, ValidationProblem>> SignOff(Guid projectId, SignOffRequest req, ReticulaDbContext db,
        ReviewReadiness readiness, IJobQueue queue, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.ArchivedAt == null, ct);
        if (project is null) return TypedResults.NotFound();
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(req.FullName) || req.FullName.Length > 200) errors["fullName"] = ["Give your full name as registered."];
        if (!RegistrationPattern().IsMatch(req.RegistrationNumber?.Trim() ?? "")) errors["registrationNumber"] = ["Give your ECSA registration number."];
        if (!req.Declaration) errors["declaration"] = [Declaration];
        if (req.Notes is { Length: > 2000 }) errors["notes"] = ["At most 2000 characters."];
        var r = await readiness.CheckAsync(projectId, ct);
        if (!r.CanSignOff) errors["readiness"] = [.. r.Blockers];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var now = time.GetUtcNow();
        var draft = await db.DocumentSets.AsNoTracking().FirstAsync(s => s.Id == r.DocumentSetId, ct);
        var number = (await db.Revisions.Where(x => x.ProjectId == projectId).MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
        var label = Revision.LabelFor(number);
        var name = req.FullName.Trim();
        var reg = req.RegistrationNumber!.Trim();
        var signedOff = $"{name}, ECSA registration {reg}, {now:yyyy-MM-dd}";
        var setNumber = (await db.DocumentSets.Where(s => s.ProjectId == projectId).MaxAsync(s => (int?)s.Number, ct) ?? 0) + 1;
        var set = new DocumentSet(Guid.CreateVersion7(), projectId, setNumber, draft.SourcesJson, draft.SourcesHash, project.RulesRef, name, userId, now, label, signedOff);
        db.DocumentSets.Add(set);
        var revision = new Revision(Guid.CreateVersion7(), projectId, number, label, set.Id, draft.Id, draft.SourcesHash, userId, name, reg, req.Notes?.Trim(), now);
        db.Revisions.Add(revision);
        await db.SaveChangesAsync(ct);
        var job = await queue.EnqueueAsync(RevisionIssueJob.JobKind, new { revisionId = revision.Id }, userId, projectId, ct);
        set.Queue(job.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted($"/api/projects/{projectId}/revisions/{revision.Id}", new StartedRevision(ToDto(revision), JobDto.From(job)));
    }

    private static async Task<Results<Ok<RevisionDto>, NotFound>> GetRevision(Guid projectId, Guid revisionId, ReticulaDbContext db, CancellationToken ct)
    {
        var r = await db.Revisions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == revisionId && x.ProjectId == projectId, ct);
        return r is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(r));
    }

    private static async Task<Results<Accepted<JobDto>, NotFound, ValidationProblem>> Reproduce(Guid projectId, Guid revisionId, ReticulaDbContext db, IJobQueue queue,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var r = await db.Revisions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == revisionId && x.ProjectId == projectId, ct);
        if (r is null) return TypedResults.NotFound();
        if (r.Status != RevisionStatus.Issued) return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Only an issued revision can be reproduced."] });
        var job = await queue.EnqueueAsync(RevisionReproduceJob.JobKind, new { revisionId }, Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), projectId, ct);
        return TypedResults.Accepted($"/api/projects/{projectId}/revisions/{revisionId}", JobDto.From(job));
    }

    private static async Task<Results<Ok<List<AuditRow>>, NotFound>> Audit(Guid projectId, string? entityType, DateTimeOffset? before, int? take, ReticulaDbContext db,
        CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct)) return TypedResults.NotFound();
        var q = db.AuditEntries.AsNoTracking().Where(a => a.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(entityType)) q = q.Where(a => a.EntityType == entityType);
        if (before is { } b) q = q.Where(a => a.At < b);
        var rows = await (
            from a in q.OrderByDescending(a => a.At).Take(Math.Clamp(take ?? 50, 1, 500))
            join u in db.Users.AsNoTracking() on a.UserId equals u.Id into us
            from u in us.DefaultIfEmpty()
            select new { a, Name = u == null ? null : u.DisplayName }).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(x => new AuditRow(x.a.At, x.Name ?? (x.a.UserId is null ? "system" : x.a.UserId.ToString()), x.a.EntityType, x.a.EntityId, x.a.Action,
            JsonDocument.Parse(x.a.ChangesJson).RootElement.Clone())).ToList());
    }

    private static async Task<Results<Accepted<StartedExport>, NotFound>> Export(Guid projectId, ReticulaDbContext db, IJobQueue queue, TimeProvider time,
        ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)) return TypedResults.NotFound();
        var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var export = new ProjectExport(Guid.CreateVersion7(), projectId, userId, time.GetUtcNow());
        db.ProjectExports.Add(export);
        await db.SaveChangesAsync(ct);
        var job = await queue.EnqueueAsync(ProjectExportJob.JobKind, new { exportId = export.Id }, userId, projectId, ct);
        export.Queue(job.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted($"/api/projects/{projectId}/review", new StartedExport(ToDto(export), JobDto.From(job)));
    }

    private static async Task<Results<Ok<DocumentLink>, NotFound>> ExportLink(Guid projectId, Guid exportId, ReticulaDbContext db, IDataProtectionProvider dp,
        TimeProvider time, CancellationToken ct)
    {
        if (!await db.ProjectExports.AnyAsync(e => e.Id == exportId && e.ProjectId == projectId && e.Status == ProjectExportStatus.Succeeded, ct)) return TypedResults.NotFound();
        var expires = time.GetUtcNow().AddMinutes(10);
        var token = dp.CreateProtector(ExportPurpose).ToTimeLimitedDataProtector().Protect(exportId.ToString(), expires);
        return TypedResults.Ok(new DocumentLink($"/api/export-files/{exportId}?token={Uri.EscapeDataString(token)}", expires));
    }

    private static async Task<Results<FileStreamHttpResult, NotFound, ForbidHttpResult>> ExportDownload(Guid exportId, string? token, ReticulaDbContext db,
        IDataProtectionProvider dp, IFileStore files, CancellationToken ct)
    {
        try
        {
            if (token is null || dp.CreateProtector(ExportPurpose).ToTimeLimitedDataProtector().Unprotect(token) != exportId.ToString()) return TypedResults.Forbid();
        }
        catch (CryptographicException)
        {
            return TypedResults.Forbid();
        }
        var e = await db.ProjectExports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == exportId, ct);
        if (e?.StorageKey is null || await files.OpenReadAsync(e.StorageKey, ct) is not { } stream) return TypedResults.NotFound();
        return TypedResults.File(stream, "application/zip", e.FileName);
    }

    private static RevisionDto ToDto(Revision r) => new(r.Id, r.Number, r.Label, r.Status.ToString().ToLowerInvariant(), r.DocumentSetId, r.SignedOffName, r.RegistrationNumber,
        r.SignedOffAt, r.Notes, r.Error, r.SnapshotSha256, r.Reproduced, r.ReproducedAt, r.ReproductionJson is null ? null : JsonDocument.Parse(r.ReproductionJson).RootElement.Clone());

    private static ExportDto ToDto(ProjectExport e) => new(e.Id, e.Status.ToString().ToLowerInvariant(), e.FileName, e.SizeBytes, e.Sha256, e.Error, e.CreatedAt, e.FinishedAt);
}
