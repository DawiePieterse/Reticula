using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Reticula.Api.Design;
using Reticula.Domain.Assistant;
using Reticula.Domain.Auth;
using Reticula.Infrastructure.Assistant;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Api.Assistant;

public sealed record AssistantStatus(bool Enabled, string? Model);

public sealed record ChatRequest(Guid? ConversationId, string Message);

public sealed record ToolCallDto(string Name, JsonElement Input, bool IsError);

public sealed record ChatResponse(Guid ConversationId, string Reply, IReadOnlyList<ToolCallDto> ToolCalls, IReadOnlyList<DraftDto> Drafts, bool Truncated);

public sealed record DraftDto(Guid Id, string Kind, JsonElement Parameters, string Explanation, string Status, Guid? RunId, DateTimeOffset CreatedAt);

public sealed record ReportSectionDto(string Key, string Title, string? Text, string? Status, string? Source, DateTimeOffset? UpdatedAt, DateTimeOffset? ApprovedAt, uint? Version);

public sealed record SaveSectionRequest(string Text, uint? Version);

/// <summary>The design assistant (plan Phase 8), its drafts, and the report's narrative sections.</summary>
public static class AssistantEndpoints
{
    public static IEndpointRouteBuilder MapAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/assistant/status", (AssistantOptions o) => TypedResults.Ok(new AssistantStatus(o.IsOn, o.IsOn ? o.Model : null)))
            .WithTags("Assistant").RequireAuthorization(Policies.FieldUser);
        var g = app.MapGroup("/api/projects/{projectId:guid}/assistant").WithTags("Assistant").RequireAuthorization(Policies.Engineer)
            .AddEndpointFilter(async (ctx, next) =>
                ctx.HttpContext.RequestServices.GetRequiredService<AssistantOptions>().IsOn
                    ? await next(ctx)
                    : TypedResults.Problem("The design assistant is not enabled.", statusCode: StatusCodes.Status404NotFound));
        g.MapPost("/messages", Chat);
        g.MapGet("/drafts", Drafts);
        g.MapPost("/drafts/{draftId:guid}/confirm", Confirm);
        g.MapPost("/drafts/{draftId:guid}/reject", Reject);

        var r = app.MapGroup("/api/projects/{projectId:guid}/report-sections").WithTags("Assistant").RequireAuthorization(Policies.FieldUser);
        r.MapGet("/", Sections);
        r.MapPut("/{key}", SaveSection).RequireAuthorization(Policies.Engineer);
        r.MapPost("/{key}/approve", Approve).RequireAuthorization(Policies.Engineer);
        return app;
    }

    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static async Task<Results<Ok<ChatResponse>, NotFound, ValidationProblem, ProblemHttpResult>> Chat(Guid projectId, ChatRequest req, ReticulaDbContext db,
        AssistantService assistant, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)) return TypedResults.NotFound();
        if (string.IsNullOrWhiteSpace(req.Message) || req.Message.Length > 4000)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["message"] = ["Write a message of up to 4000 characters."] });
        try
        {
            var before = DateTimeOffset.UtcNow.AddSeconds(-1);
            var reply = await assistant.ChatAsync(projectId, UserId(user), req.ConversationId, req.Message.Trim(), ct);
            var drafts = await db.AssistantDrafts.AsNoTracking().Where(d => d.ConversationId == reply.ConversationId && d.CreatedAt >= before).ToListAsync(ct);
            return TypedResults.Ok(new ChatResponse(reply.ConversationId, reply.Reply, [.. reply.ToolCalls.Select(c => new ToolCallDto(c.Name, c.Input, c.IsError))],
                [.. drafts.Select(ToDto)], reply.Truncated));
        }
        catch (InvalidOperationException e)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["conversationId"] = [e.Message] });
        }
        catch (AssistantUnavailableException e)
        {
            return TypedResults.Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<Ok<List<DraftDto>>> Drafts(Guid projectId, ReticulaDbContext db, CancellationToken ct) =>
        TypedResults.Ok((await db.AssistantDrafts.AsNoTracking().Where(d => d.ProjectId == projectId).OrderByDescending(d => d.CreatedAt).Take(50).ToListAsync(ct)).Select(ToDto).ToList());

    /// <summary>The engineer confirms a draft: it starts exactly as if they had filled in the run form, with the same checks.</summary>
    private static async Task<Results<Ok<DraftDto>, NotFound, ValidationProblem>> Confirm(Guid projectId, Guid draftId, ReticulaDbContext db, IJobQueue queue,
        TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var draft = await db.AssistantDrafts.FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
        if (draft is null) return TypedResults.NotFound();
        if (draft.Status != DraftStatus.Proposed) return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["This draft was already decided."] });
        var p = JsonSerializer.Deserialize<JsonElement>(draft.ParametersJson);
        string? S(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? D(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        List<string>? L(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Select(x => x.GetString() ?? "")] : null;
        var site = Guid.TryParse(S("transformerCandidateId"), out var g) ? g : Guid.Empty;
        var result = draft.Kind switch
        {
            DraftKinds.LvDesign => await DesignEndpoints.Start(projectId, new LvDesignRequest(site, L("constructions"), D("transformerKva"), null), db, queue, time, user, ct),
            DraftKinds.MvDesign => await DesignEndpoints.StartMv(projectId, new MvDesignRequest(null, S("lvConstruction"), S("mvConstruction"), null), db, queue, time, user, ct),
            _ => await DesignEndpoints.StartOptions(projectId, new OptionSearchRequest(site, L("constructions"), L("objectives"), D("capexCeiling"), null,
                p.TryGetProperty("allowMove", out var am) && am.ValueKind is JsonValueKind.True or JsonValueKind.False ? am.GetBoolean() : null, D("moveRadiusM"), null), db, queue, time, user, ct),
        };
        switch (result.Result)
        {
            case Accepted<StartedDesign> started:
                draft.Confirm(UserId(user), started.Value!.Run.Id, time.GetUtcNow());
                await db.SaveChangesAsync(ct);
                return TypedResults.Ok(ToDto(draft));
            case ValidationProblem problem:
                return problem;
            default:
                return TypedResults.NotFound();
        }
    }

    private static async Task<Results<Ok<DraftDto>, NotFound>> Reject(Guid projectId, Guid draftId, ReticulaDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var draft = await db.AssistantDrafts.FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == projectId && d.Status == DraftStatus.Proposed, ct);
        if (draft is null) return TypedResults.NotFound();
        draft.Reject(UserId(user), time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(draft));
    }

    private static async Task<Results<Ok<List<ReportSectionDto>>, NotFound>> Sections(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct)) return TypedResults.NotFound();
        var rows = await db.ReportSections.AsNoTracking().Where(s => s.ProjectId == projectId).ToDictionaryAsync(s => s.Key, ct);
        return TypedResults.Ok(ReportSectionKeys.Titles.Select(kv => rows.TryGetValue(kv.Key, out var s)
            ? new ReportSectionDto(kv.Key, kv.Value, s.Text, s.Status.ToString().ToLowerInvariant(), s.Source, s.UpdatedAt, s.ApprovedAt, s.Version)
            : new ReportSectionDto(kv.Key, kv.Value, null, null, null, null, null, null)).ToList());
    }

    private static async Task<Results<Ok<ReportSectionDto>, NotFound, ValidationProblem, Conflict>> SaveSection(Guid projectId, string key, SaveSectionRequest req, ReticulaDbContext db,
        TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!ReportSectionKeys.Titles.TryGetValue(key, out var title)) return TypedResults.NotFound();
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)) return TypedResults.NotFound();
        if (string.IsNullOrWhiteSpace(req.Text) || req.Text.Length > 20000)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["text"] = ["Write up to 20000 characters."] });
        var s = await db.ReportSections.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Key == key, ct);
        if (s is not null && req.Version is { } v && v != s.Version) return TypedResults.Conflict();
        if (s is null) db.ReportSections.Add(s = new ReportSection(projectId, key, req.Text.Trim(), "engineer", UserId(user), time.GetUtcNow()));
        else s.Edit(req.Text.Trim(), "engineer", UserId(user), time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new ReportSectionDto(key, title, s.Text, s.Status.ToString().ToLowerInvariant(), s.Source, s.UpdatedAt, s.ApprovedAt, s.Version));
    }

    private static async Task<Results<Ok<ReportSectionDto>, NotFound>> Approve(Guid projectId, string key, ReticulaDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var s = await db.ReportSections.FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Key == key, ct);
        if (s is null || !ReportSectionKeys.Titles.TryGetValue(key, out var title)) return TypedResults.NotFound();
        s.Approve(UserId(user), time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new ReportSectionDto(key, title, s.Text, s.Status.ToString().ToLowerInvariant(), s.Source, s.UpdatedAt, s.ApprovedAt, s.Version));
    }

    private static DraftDto ToDto(AssistantDraft d) => new(d.Id, d.Kind, JsonSerializer.Deserialize<JsonElement>(d.ParametersJson), d.Explanation,
        d.Status.ToString().ToLowerInvariant(), d.RunId, d.CreatedAt);
}
