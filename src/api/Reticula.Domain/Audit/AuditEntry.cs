namespace Reticula.Domain.Audit;

/// <summary>Who changed what, when, with the values before and after (plan 7.4). Written with the change itself.</summary>
public sealed class AuditEntry
{
    private AuditEntry() { } // EF

    public AuditEntry(Guid id, Guid? projectId, Guid? userId, string entityType, string entityId, string action, string changesJson, DateTimeOffset at)
    {
        Id = id;
        ProjectId = projectId;
        UserId = userId;
        EntityType = entityType;
        EntityId = entityId;
        Action = action;
        ChangesJson = changesJson;
        At = at;
    }

    public Guid Id { get; private set; }
    public Guid? ProjectId { get; private set; }
    /// <summary>The user, or the user who started the background job; null for the system.</summary>
    public Guid? UserId { get; private set; }
    public string EntityType { get; private set; } = "";
    public string EntityId { get; private set; } = "";
    /// <summary>created, updated or deleted.</summary>
    public string Action { get; private set; } = "";
    /// <summary>{"Property": [before, after], …}; before is null when created, after is null when deleted.</summary>
    public string ChangesJson { get; private set; } = "{}";
    public DateTimeOffset At { get; private set; }
}
