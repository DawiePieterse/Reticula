namespace Reticula.Domain.Assistant;

public static class AssistantDraftKinds
{
    public const string RunParameters = "run_parameters";
    public const string ReportSection = "report_section";
}

public static class AssistantDraftStatus
{
    public const string Proposed = "proposed";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

/// <summary>
/// Something the design assistant proposed for the engineer to accept or reject (plan 8.2). The assistant never acts on its own: a
/// draft only takes effect when the engineer accepts it.
/// </summary>
public sealed class AssistantDraft
{
    private AssistantDraft() { } // EF

    public AssistantDraft(Guid id, Guid projectId, Guid conversationId, string kind, string payloadJson, string explanation, DateTimeOffset now)
    {
        Id = id;
        ProjectId = projectId;
        ConversationId = conversationId;
        Kind = kind;
        PayloadJson = payloadJson;
        Explanation = explanation;
        Status = AssistantDraftStatus.Proposed;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ConversationId { get; private set; }
    public string Kind { get; private set; } = "";
    public string PayloadJson { get; private set; } = "{}";
    public string Explanation { get; private set; } = "";
    public string Status { get; private set; } = AssistantDraftStatus.Proposed;
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    public void Decide(bool accept, Guid by, DateTimeOffset now)
    {
        Status = accept ? AssistantDraftStatus.Accepted : AssistantDraftStatus.Rejected;
        DecidedBy = by;
        DecidedAt = now;
    }
}

/// <summary>One user's conversation with the assistant about a project; the messages as the model API takes them.</summary>
public sealed class AssistantConversation
{
    private AssistantConversation() { } // EF

    public AssistantConversation(Guid id, Guid projectId, Guid userId, DateTimeOffset now)
    {
        Id = id;
        ProjectId = projectId;
        UserId = userId;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid UserId { get; private set; }
    public string MessagesJson { get; private set; } = "[]";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Save(string messagesJson, DateTimeOffset now)
    {
        MessagesJson = messagesJson;
        UpdatedAt = now;
    }
}
