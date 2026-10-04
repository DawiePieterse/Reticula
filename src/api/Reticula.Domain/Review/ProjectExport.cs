namespace Reticula.Domain.Review;

public enum ProjectExportStatus { Queued, Running, Succeeded, Failed }

/// <summary>The whole project in open formats (plan 7.5): GeoJSON, JSON and the stored files, zipped.</summary>
public sealed class ProjectExport
{
    private ProjectExport() { } // EF

    public ProjectExport(Guid id, Guid projectId, Guid createdBy, DateTimeOffset now)
    {
        Id = id;
        ProjectId = projectId;
        CreatedBy = createdBy;
        CreatedAt = now;
        Status = ProjectExportStatus.Queued;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public ProjectExportStatus Status { get; private set; }
    public Guid? JobId { get; private set; }
    public string? StorageKey { get; private set; }
    public string? FileName { get; private set; }
    public long SizeBytes { get; private set; }
    public string? Sha256 { get; private set; }
    public string? Error { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public void Queue(Guid jobId) => JobId = jobId;
    public void Start() => Status = ProjectExportStatus.Running;

    public void Succeed(string storageKey, string fileName, long size, string sha256, DateTimeOffset now)
    {
        Status = ProjectExportStatus.Succeeded;
        StorageKey = storageKey;
        FileName = fileName;
        SizeBytes = size;
        Sha256 = sha256;
        FinishedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Status = ProjectExportStatus.Failed;
        Error = error;
        FinishedAt = now;
    }
}
