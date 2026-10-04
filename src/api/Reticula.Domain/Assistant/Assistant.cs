namespace Reticula.Domain.Assistant;

/// <summary>A conversation with the design assistant (plan Phase 8), kept so it can continue.</summary>
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
    /// <summary>The Messages API transcript (user, assistant, tool use and tool result blocks).</summary>
    public string MessagesJson { get; private set; } = "[]";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Save(string messagesJson, DateTimeOffset now)
    {
        MessagesJson = messagesJson;
        UpdatedAt = now;
    }
}

public enum DraftStatus { Proposed, Confirmed, Rejected }

public static class DraftKinds
{
    public const string LvDesign = "lv_design";
    public const string MvDesign = "mv_design";
    public const string OptionSearch = "option_search";
}

/// <summary>
/// Run parameters drafted by the assistant (plan 8.2). Nothing runs until an engineer confirms it; the confirmed run
/// goes through the same validation as one started by hand.
/// </summary>
public sealed class AssistantDraft
{
    private AssistantDraft() { } // EF

    public AssistantDraft(Guid id, Guid projectId, Guid conversationId, string kind, string parametersJson, string explanation, DateTimeOffset now)
    {
        Id = id;
        ProjectId = projectId;
        ConversationId = conversationId;
        Kind = kind;
        ParametersJson = parametersJson;
        Explanation = explanation;
        CreatedAt = now;
        Status = DraftStatus.Proposed;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ConversationId { get; private set; }
    public string Kind { get; private set; } = "";
    public string ParametersJson { get; private set; } = "{}";
    public string Explanation { get; private set; } = "";
    public DraftStatus Status { get; private set; }
    public Guid? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    /// <summary>The design run started when confirmed.</summary>
    public Guid? RunId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public void Confirm(Guid by, Guid runId, DateTimeOffset now)
    {
        Status = DraftStatus.Confirmed;
        DecidedBy = by;
        DecidedAt = now;
        RunId = runId;
    }

    public void Reject(Guid by, DateTimeOffset now)
    {
        Status = DraftStatus.Rejected;
        DecidedBy = by;
        DecidedAt = now;
    }
}

public enum ReportSectionStatus { Draft, Approved }

/// <summary>
/// A narrative section of the design report (plan 8.4), drafted by the assistant or written by the engineer. Only
/// approved sections are printed; any edit returns a section to draft.
/// </summary>
public sealed class ReportSection
{
    private ReportSection() { } // EF

    public ReportSection(Guid projectId, string key, string text, string source, Guid by, DateTimeOffset now)
    {
        ProjectId = projectId;
        Key = key;
        Edit(text, source, by, now);
    }

    public Guid ProjectId { get; private set; }
    public string Key { get; private set; } = "";
    public string Text { get; private set; } = "";
    /// <summary>assistant or engineer: who wrote the current text.</summary>
    public string Source { get; private set; } = "";
    public ReportSectionStatus Status { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }

    /// <summary>Optimistic concurrency token (Postgres xmin).</summary>
    public uint Version { get; private set; }

    public void Edit(string text, string source, Guid by, DateTimeOffset now)
    {
        Text = text;
        Source = source;
        Status = ReportSectionStatus.Draft;
        UpdatedBy = by;
        UpdatedAt = now;
        ApprovedBy = null;
        ApprovedAt = null;
    }

    public void Approve(Guid by, DateTimeOffset now)
    {
        Status = ReportSectionStatus.Approved;
        ApprovedBy = by;
        ApprovedAt = now;
    }
}

public static class ReportSectionKeys
{
    public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>
    {
        ["introduction"] = "Introduction",
        ["site_description"] = "Site description",
        ["design_approach"] = "Design approach",
        ["options_discussion"] = "Options considered",
        ["conclusions"] = "Conclusions and recommendations",
    };
}
