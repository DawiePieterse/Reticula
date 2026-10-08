namespace Reticula.Domain.Review;

/// <summary>
/// A numbered issue of the design (plan 7.2): a snapshot of one design run, whose stored request reproduces it bit for bit. Signing
/// off (plan 7.3) records the registered engineer and locks the revision and its documents.
/// </summary>
public sealed class Revision
{
    private Revision() { } // EF

    public Revision(Guid id, Guid projectId, int number, string label, string? description, Guid designRunId, string rulesRef, string rulesHash,
        string inputsHash, string resultHash, bool fitToSubmit, Guid createdBy, DateTimeOffset now)
    {
        Id = id;
        ProjectId = projectId;
        Number = number;
        Label = label;
        Description = description;
        DesignRunId = designRunId;
        RulesRef = rulesRef;
        RulesHash = rulesHash;
        InputsHash = inputsHash;
        ResultHash = resultHash;
        FitToSubmit = fitToSubmit;
        CreatedBy = createdBy;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public int Number { get; private set; }
    public string Label { get; private set; } = "";
    public string? Description { get; private set; }
    public Guid DesignRunId { get; private set; }
    public string RulesRef { get; private set; } = "";
    public string RulesHash { get; private set; } = "";
    public string InputsHash { get; private set; } = "";
    public string ResultHash { get; private set; } = "";
    public bool FitToSubmit { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? SignedOffBy { get; private set; }
    public DateTimeOffset? SignedOffAt { get; private set; }
    public string? EngineerName { get; private set; }
    public string? RegistrationNo { get; private set; }
    public string? SignOffStatement { get; private set; }
    /// <summary>The last reproduction: whether the stored request gave the same result bit for bit.</summary>
    public bool? Reproduced { get; private set; }
    public DateTimeOffset? ReproducedAt { get; private set; }

    public bool Locked => SignedOffAt is not null;

    public void SignOff(Guid by, string engineerName, string registrationNo, string statement, DateTimeOffset now)
    {
        SignedOffBy = by;
        EngineerName = engineerName;
        RegistrationNo = registrationNo;
        SignOffStatement = statement;
        SignedOffAt = now;
    }

    public void RecordReproduction(bool identical, DateTimeOffset now)
    {
        Reproduced = identical;
        ReproducedAt = now;
    }
}
