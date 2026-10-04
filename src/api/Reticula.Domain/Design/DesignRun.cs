namespace Reticula.Domain.Design;

public enum DesignRunStatus { Queued, Running, Succeeded, Failed }

/// <summary>
/// One design calculation with everything needed to read it later: its parameters, the rules version and hash, the
/// input it was given and the calc service's full result (network, traced values, checks). Results are never edited;
/// a new run replaces an old one.
/// </summary>
public sealed class DesignRun
{
    private DesignRun() { } // EF

    public DesignRun(Guid id, Guid projectId, string kind, string parametersJson, string rulesRef, Guid createdBy, DateTimeOffset now)
    {
        Id = id;
        ProjectId = projectId;
        Kind = kind;
        ParametersJson = parametersJson;
        RulesRef = rulesRef;
        CreatedBy = createdBy;
        CreatedAt = now;
        Status = DesignRunStatus.Queued;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }

    /// <summary>One of <see cref="DesignKinds"/>.</summary>
    public string Kind { get; private set; } = "";
    public DesignRunStatus Status { get; private set; }
    public Guid? JobId { get; private set; }
    public string ParametersJson { get; private set; } = "{}";
    public string RulesRef { get; private set; } = "";
    public string? RulesHash { get; private set; }
    public string? InputJson { get; private set; }
    public string? ResultJson { get; private set; }

    /// <summary>All checks of the first (or only) option passed.</summary>
    public bool? Passed { get; private set; }
    public string? SummaryJson { get; private set; }
    public string? Error { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public void Queue(Guid jobId) => JobId = jobId;

    public void Start() => Status = DesignRunStatus.Running;

    public void Succeed(string inputJson, string resultJson, string rulesHash, bool passed, string summaryJson, DateTimeOffset now)
    {
        Status = DesignRunStatus.Succeeded;
        InputJson = inputJson;
        ResultJson = resultJson;
        RulesHash = rulesHash;
        Passed = passed;
        SummaryJson = summaryJson;
        FinishedAt = now;
    }

    public void Fail(string error, string? inputJson, DateTimeOffset now)
    {
        Status = DesignRunStatus.Failed;
        Error = error;
        InputJson = inputJson;
        FinishedAt = now;
    }
}

public static class DesignKinds
{
    public const string Lv = "lv";
    public const string Mv = "mv";
    public const string Bulk = "bulk";
    public const string Options = "options";
}
