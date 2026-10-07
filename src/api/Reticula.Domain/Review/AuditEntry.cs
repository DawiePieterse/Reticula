namespace Reticula.Domain.Review;

/// <summary>Who changed what, when, with the values before and after (plan 7.4). Written by the database context on every save.</summary>
public sealed class AuditEntry
{
    private AuditEntry() { } // EF

    public AuditEntry(Guid id, DateTimeOffset at, Guid? userId, Guid? projectId, string entityType, string entityId, string action,
        string? beforeJson, string? afterJson)
    {
        Id = id;
        At = at;
        UserId = userId;
        ProjectId = projectId;
        EntityType = entityType;
        EntityId = entityId;
        Action = action;
        BeforeJson = beforeJson;
        AfterJson = afterJson;
    }

    public Guid Id { get; private set; }
    public DateTimeOffset At { get; private set; }
    /// <summary>The signed-in user, or the user who started the background job; null for system changes.</summary>
    public Guid? UserId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public string EntityType { get; private set; } = "";
    public string EntityId { get; private set; } = "";
    /// <summary>added, modified or deleted.</summary>
    public string Action { get; private set; } = "";
    /// <summary>The changed properties' old values (modified) or every value (deleted).</summary>
    public string? BeforeJson { get; private set; }
    public string? AfterJson { get; private set; }
}
