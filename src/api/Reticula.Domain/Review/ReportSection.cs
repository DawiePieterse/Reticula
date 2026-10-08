namespace Reticula.Domain.Review;

public static class ReportSectionStatus
{
    public const string Draft = "draft";
    public const string Approved = "approved";
}

/// <summary>
/// A text section of the design report (plan 8.4): drafted by the engineer or by the assistant, edited, and only printed once the
/// engineer approves it. Any edit makes it a draft again.
/// </summary>
public sealed class ReportSection
{
    private ReportSection() { } // EF

    public ReportSection(Guid id, Guid projectId, string key, string title, string text, string source, Guid by, DateTimeOffset now)
    {
        Id = id;
        ProjectId = projectId;
        Key = key;
        Title = title;
        Text = text;
        Source = source;
        Status = ReportSectionStatus.Draft;
        UpdatedBy = by;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Key { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Text { get; private set; } = "";
    /// <summary>engineer or assistant: who wrote the current text.</summary>
    public string Source { get; private set; } = "engineer";
    public string Status { get; private set; } = ReportSectionStatus.Draft;
    public int Order { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public uint Version { get; private set; }

    public void Edit(string title, string text, string source, int order, Guid by, DateTimeOffset now)
    {
        Title = title;
        Text = text;
        Source = source;
        Order = order;
        Status = ReportSectionStatus.Draft;
        ApprovedBy = null;
        ApprovedAt = null;
        UpdatedBy = by;
        UpdatedAt = now;
    }

    public void Approve(Guid by, DateTimeOffset now)
    {
        Status = ReportSectionStatus.Approved;
        ApprovedBy = by;
        ApprovedAt = now;
    }
}
