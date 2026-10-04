namespace Reticula.Domain.Documents;

public enum DocumentSetStatus { Queued, Running, Succeeded, Failed }

/// <summary>
/// One "Generate all" (plan 6.7): every document made together from the same design runs, numbered as a draft
/// revision (D1, D2 …) until sign-off. The sources it was made from are kept so later changes mark it stale.
/// </summary>
public sealed class DocumentSet
{
    private DocumentSet() { } // EF

    public DocumentSet(Guid id, Guid projectId, int number, string sourcesJson, string sourcesHash, string rulesRef, string? engineer, Guid createdBy, DateTimeOffset now)
    {
        Id = id;
        ProjectId = projectId;
        Number = number;
        Revision = $"D{number}";
        SourcesJson = sourcesJson;
        SourcesHash = sourcesHash;
        RulesRef = rulesRef;
        Engineer = engineer;
        CreatedBy = createdBy;
        CreatedAt = now;
        Status = DocumentSetStatus.Queued;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public int Number { get; private set; }
    public string Revision { get; private set; } = "";
    public DocumentSetStatus Status { get; private set; }
    public Guid? JobId { get; private set; }
    public string SourcesJson { get; private set; } = "{}";
    public string SourcesHash { get; private set; } = "";
    public string RulesRef { get; private set; } = "";
    public string? Engineer { get; private set; }
    public string? ChecklistJson { get; private set; }
    public string? WarningsJson { get; private set; }
    public string? Error { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public void Queue(Guid jobId) => JobId = jobId;
    public void Start() => Status = DocumentSetStatus.Running;

    public void Succeed(string checklistJson, string warningsJson, DateTimeOffset now)
    {
        Status = DocumentSetStatus.Succeeded;
        ChecklistJson = checklistJson;
        WarningsJson = warningsJson;
        FinishedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Status = DocumentSetStatus.Failed;
        Error = error;
        FinishedAt = now;
    }
}

/// <summary>One generated file of a document set, kept in the file store.</summary>
public sealed class ProjectDocument
{
    private ProjectDocument() { } // EF

    public ProjectDocument(Guid id, Guid setId, Guid projectId, string kind, string fileName, string title, string contentType, string storageKey,
        long sizeBytes, string sha256, DateTimeOffset now)
    {
        Id = id;
        SetId = setId;
        ProjectId = projectId;
        Kind = kind;
        FileName = fileName;
        Title = title;
        ContentType = contentType;
        StorageKey = storageKey;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid SetId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Kind { get; private set; } = "";
    public string FileName { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public string StorageKey { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
}
