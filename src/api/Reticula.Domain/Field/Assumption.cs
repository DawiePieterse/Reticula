namespace Reticula.Domain.Field;

public enum AssumptionStatus
{
    Open,
    Cleared,
}

public static class AssumptionCodes
{
    public const string AdmdEstimated = "admd_estimated";
    public const string AdmdOverridden = "admd_overridden";
    public const string IndicatorsMissing = "indicators_missing";
}

/// <summary>
/// An estimate or unchecked item the design relies on. All must be cleared before sign-off.
/// Each (subject, code) pair has at most one row; re-estimating reopens it.
/// </summary>
public sealed class Assumption
{
    private Assumption() { } // EF

    public Assumption(Guid id, Guid projectId, string subjectType, Guid subjectId, string code, string text, DateTimeOffset now)
    {
        Id = id;
        ProjectId = projectId;
        SubjectType = subjectType;
        SubjectId = subjectId;
        Code = code;
        Text = text;
        Status = AssumptionStatus.Open;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string SubjectType { get; private set; } = "";
    public Guid SubjectId { get; private set; }
    public string Code { get; private set; } = "";
    public string Text { get; private set; } = "";
    public AssumptionStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid? ClearedBy { get; private set; }
    public DateTimeOffset? ClearedAt { get; private set; }
    public string? ClearNote { get; private set; }

    public void Reopen(string text, DateTimeOffset now)
    {
        Text = text;
        Status = AssumptionStatus.Open;
        ClearedBy = null;
        ClearedAt = null;
        ClearNote = null;
        UpdatedAt = now;
    }

    public void Clear(Guid by, string? note, DateTimeOffset now)
    {
        Status = AssumptionStatus.Cleared;
        ClearedBy = by;
        ClearedAt = now;
        ClearNote = note;
        UpdatedAt = now;
    }
}
