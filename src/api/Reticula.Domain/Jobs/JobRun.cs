namespace Reticula.Domain.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Application-level record of a background job: what ran, for whom, how far it got and how it ended.</summary>
public sealed class JobRun
{
    private JobRun() { } // EF

    public JobRun(Guid id, string kind, string payloadJson, Guid requestedBy, Guid? projectId, DateTimeOffset now)
    {
        Id = id;
        Kind = kind;
        PayloadJson = payloadJson;
        RequestedBy = requestedBy;
        ProjectId = projectId;
        CreatedAt = now;
        Status = JobStatus.Queued;
    }

    public Guid Id { get; private set; }
    public string Kind { get; private set; } = "";
    public JobStatus Status { get; private set; }
    public int ProgressPct { get; private set; }
    public string? Message { get; private set; }
    public Guid? ProjectId { get; private set; }
    public Guid RequestedBy { get; private set; }
    public string PayloadJson { get; private set; } = "{}";
    public string? ResultJson { get; private set; }
    public string? Error { get; private set; }
    public string? BackgroundJobId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public bool IsFinished => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;

    public void Start(DateTimeOffset now)
    {
        Status = JobStatus.Running;
        StartedAt = now;
        Message = "Started";
    }

    public void Report(int pct, string message)
    {
        if (IsFinished) return;
        ProgressPct = Math.Clamp(pct, 0, 100);
        Message = message;
    }

    public void Succeed(string? resultJson, DateTimeOffset now)
    {
        if (IsFinished) return;
        Status = JobStatus.Succeeded;
        ProgressPct = 100;
        ResultJson = resultJson;
        Message = "Done";
        FinishedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        if (IsFinished) return;
        Status = JobStatus.Failed;
        Error = error;
        Message = "Failed";
        FinishedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (IsFinished) return;
        Status = JobStatus.Cancelled;
        Message = "Cancelled";
        FinishedAt = now;
    }
}
