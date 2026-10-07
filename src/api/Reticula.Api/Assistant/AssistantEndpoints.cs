using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Reticula.Api.Infrastructure;
using Reticula.Api.Jobs;
using Reticula.Api.Review;
using Reticula.Domain.Assistant;
using Reticula.Domain.Auth;
using Reticula.Domain.Review;
using Reticula.Infrastructure.Assistant;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Api.Assistant;

public sealed record AssistantStatus(bool Enabled, string? Model, string? Reason);

public sealed record AssistantMessageRequest(Guid? ConversationId, string Text);

public sealed record AssistantDraftDto(Guid Id, string Kind, JsonElement Payload, string Explanation, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt)
{
    public static AssistantDraftDto From(AssistantDraft d) =>
        new(d.Id, d.Kind, JsonDocument.Parse(d.PayloadJson).RootElement.Clone(), d.Explanation, d.Status, d.CreatedAt, d.DecidedAt);
}

public sealed record AssistantReplyDto(Guid ConversationId, string Text, IReadOnlyList<AssistantDraftDto> Drafts, IReadOnlyList<ToolCallRecord> ToolCalls);

public sealed record ConversationSummary(Guid Id, string Title, int Messages, DateTimeOffset UpdatedAt);

/// <param name="Tools">Tool names the assistant called in this turn.</param>
public sealed record ConversationTurn(string Role, string Text, IReadOnlyList<string> Tools);

/// <param name="Job">For run parameters: the design run the engineer started by accepting.</param>
/// <param name="Section">For report text: the section it went into, as a draft to edit and approve.</param>
public sealed record DraftDecision(AssistantDraftDto Draft, JobDto? Job, ReportSectionDto? Section);

/// <summary>
/// The design assistant (Phase 8), optional and off by default (plan 8.5). It reads the project through its tool gateway and proposes;
/// only the engineer's accept puts a proposal into effect.
/// </summary>
public static class AssistantEndpoints
{
    public static IEndpointRouteBuilder MapAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/assistant/status", (AssistantSettings s) => TypedResults.Ok(new AssistantStatus(s.Unavailable is null, s.Unavailable is null ? s.Model : null, s.Unavailable)))
            .WithTags("Assistant").RequireAuthorization();
        var g = app.MapGroup("/api/projects/{projectId:guid}/assistant").WithTags("Assistant").RequireAuthorization(Policies.Engineer);
        g.MapPost("/messages", Send);
        g.MapGet("/conversations", Conversations);
        g.MapGet("/conversations/{conversationId:guid}", Conversation);
        g.MapGet("/drafts", Drafts);
        g.MapPost("/drafts/{draftId:guid}/accept", Accept);
        g.MapPost("/drafts/{draftId:guid}/reject", Reject);
        return app;
    }

    private static async Task<Results<Ok<AssistantReplyDto>, NotFound, ValidationProblem, ProblemHttpResult>> Send(Guid projectId, AssistantMessageRequest req,
        ReticulaDbContext db, AssistantSettings settings, AssistantService assistant, ClaimsPrincipal user, CancellationToken ct)
    {
        if (settings.Unavailable is { } why) return TypedResults.Problem(title: why, statusCode: StatusCodes.Status404NotFound);
        var project = await db.Projects.ActiveAsync(projectId, ct);
        if (project is null) return TypedResults.NotFound();
        if (string.IsNullOrWhiteSpace(req.Text) || req.Text.Length > AssistantService.MaxMessageLength)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["text"] = [$"A message of at most {AssistantService.MaxMessageLength} characters."] });
        try
        {
            var reply = await assistant.SendAsync(project, user.UserId(), req.ConversationId, req.Text.Trim(), ct);
            return TypedResults.Ok(new AssistantReplyDto(reply.ConversationId, reply.Text, [.. reply.Drafts.Select(AssistantDraftDto.From)], reply.ToolCalls));
        }
        catch (KeyNotFoundException)
        {
            return TypedResults.NotFound();
        }
        catch (InvalidOperationException e)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["conversationId"] = [e.Message] });
        }
        catch (AssistantUnavailableException e)
        {
            return TypedResults.Problem(title: "The design assistant is unavailable", detail: e.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<Ok<List<ConversationSummary>>> Conversations(Guid projectId, ReticulaDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        var rows = await db.AssistantConversations.AsNoTracking().Where(c => c.ProjectId == projectId && c.UserId == user.UserId())
            .OrderByDescending(c => c.UpdatedAt).Take(50).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(c =>
        {
            var turns = Turns(c.MessagesJson);
            var first = turns.FirstOrDefault(t => t.Role == "user")?.Text ?? "";
            return new ConversationSummary(c.Id, first.Length > 80 ? first[..80] + "…" : first, turns.Count, c.UpdatedAt);
        }).ToList());
    }

    private static async Task<Results<Ok<List<ConversationTurn>>, NotFound>> Conversation(Guid projectId, Guid conversationId, ReticulaDbContext db,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var c = await db.AssistantConversations.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == conversationId && x.ProjectId == projectId && x.UserId == user.UserId(), ct);
        return c is null ? TypedResults.NotFound() : TypedResults.Ok(Turns(c.MessagesJson));
    }

    /// <summary>The conversation as people read it: the engineer's messages and the assistant's words, tool results left out.</summary>
    private static List<ConversationTurn> Turns(string messagesJson)
    {
        var turns = new List<ConversationTurn>();
        foreach (var m in JsonNode.Parse(messagesJson)!.AsArray().OfType<JsonObject>())
        {
            var blocks = m["content"] as JsonArray ?? [];
            var text = string.Join("\n\n", blocks.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "text").Select(b => b["text"]?.GetValue<string>() ?? ""));
            var tools = blocks.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "tool_use").Select(b => b["name"]?.GetValue<string>() ?? "").ToList();
            if (text.Length > 0 || tools.Count > 0) turns.Add(new ConversationTurn(m["role"]?.GetValue<string>() ?? "", text, tools));
        }
        return turns;
    }

    private static async Task<Ok<List<AssistantDraftDto>>> Drafts(Guid projectId, string? status, ReticulaDbContext db, CancellationToken ct)
    {
        var q = db.AssistantDrafts.AsNoTracking().Where(d => d.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(d => d.Status == status);
        return TypedResults.Ok((await q.OrderByDescending(d => d.CreatedAt).Take(100).ToListAsync(ct)).Select(AssistantDraftDto.From).ToList());
    }

    private static async Task<Results<Ok<DraftDecision>, Accepted<DraftDecision>, NotFound, ValidationProblem>> Accept(Guid projectId, Guid draftId,
        ReticulaDbContext db, IJobQueue queue, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var draft = await db.AssistantDrafts.FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
        if (draft is null || !await db.Projects.AnyActiveAsync(projectId, ct)) return TypedResults.NotFound();
        if (draft.Status != AssistantDraftStatus.Proposed)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["draftId"] = [$"The draft was already {draft.Status}."] });
        using var payload = JsonDocument.Parse(draft.PayloadJson);
        var p = payload.RootElement;
        var now = time.GetUtcNow();
        if (draft.Kind == AssistantDraftKinds.RunParameters)
        {
            JsonElement? Opt(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v.Clone() : null;
            var mode = p.GetProperty("mode").GetString()!;
            var errors = RunOptions.ValidateDesign(Opt("options"));
            if (mode == "optimise") foreach (var e in RunOptions.ValidateOptimise(Opt("optimise"))) errors[e.Key] = e.Value;
            if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
            draft.Decide(true, user.UserId(), now);
            await db.SaveChangesAsync(ct);
            var job = await queue.EnqueueAsync(DesignRunJob.JobKind, new DesignRunPayload(mode, Opt("options"), mode == "optimise" ? Opt("optimise") : null),
                user.UserId(), projectId, ct);
            return TypedResults.Accepted($"/api/jobs/{job.Id}", new DraftDecision(AssistantDraftDto.From(draft), JobDto.From(job), null));
        }

        var key = p.GetProperty("key").GetString()!;
        var section = await db.ReportSections.FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Key == key, ct);
        if (section is null)
        {
            section = new ReportSection(Guid.CreateVersion7(), projectId, key, p.GetProperty("title").GetString()!, p.GetProperty("text").GetString()!,
                "assistant", user.UserId(), now);
            db.ReportSections.Add(section);
        }
        else section.Edit(p.GetProperty("title").GetString()!, p.GetProperty("text").GetString()!, "assistant", section.Order, user.UserId(), now);
        draft.Decide(true, user.UserId(), now);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new DraftDecision(AssistantDraftDto.From(draft), null, new ReportSectionDto(section.Id, section.Key, section.Title, section.Text,
            section.Source, section.Status, section.Order, section.UpdatedAt, section.ApprovedAt, section.Version)));
    }

    private static async Task<Results<Ok<AssistantDraftDto>, NotFound, ValidationProblem>> Reject(Guid projectId, Guid draftId, ReticulaDbContext db,
        TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var draft = await db.AssistantDrafts.FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
        if (draft is null) return TypedResults.NotFound();
        if (draft.Status != AssistantDraftStatus.Proposed)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["draftId"] = [$"The draft was already {draft.Status}."] });
        draft.Decide(false, user.UserId(), time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(AssistantDraftDto.From(draft));
    }
}
