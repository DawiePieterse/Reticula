namespace Reticula.Domain.Review;

public enum RevisionStatus { Issuing, Issued, Failed }

/// <summary>
/// An issued, signed-off revision of the design (plans 7.2, 7.3): a lettered document set (A, B …) generated from
/// sources that had no open assumptions and no failed checks, a snapshot of the field data and inputs it rests on,
/// and the signing engineer's name and ECSA registration. Its documents are locked; later changes need a new revision.
/// </summary>
public sealed class Revision
{
    private Revision() { } // EF

    public Revision(Guid id, Guid projectId, int number, string label, Guid documentSetId, Guid basedOnSetId, string sourcesHash, Guid signedOffBy,
        string signedOffName, string registrationNumber, string? notes, DateTimeOffset now)
    {
        Id = id;
        ProjectId = projectId;
        Number = number;
        Label = label;
        DocumentSetId = documentSetId;
        BasedOnSetId = basedOnSetId;
        SourcesHash = sourcesHash;
        SignedOffBy = signedOffBy;
        SignedOffName = signedOffName;
        RegistrationNumber = registrationNumber;
        Notes = notes;
        SignedOffAt = now;
        Status = RevisionStatus.Issuing;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public int Number { get; private set; }
    /// <summary>A, B, … Z, AA, AB …</summary>
    public string Label { get; private set; } = "";
    public RevisionStatus Status { get; private set; }
    public Guid DocumentSetId { get; private set; }
    /// <summary>The draft document set the engineer reviewed.</summary>
    public Guid BasedOnSetId { get; private set; }
    public string SourcesHash { get; private set; } = "";
    public string? SnapshotKey { get; private set; }
    public string? SnapshotSha256 { get; private set; }
    public Guid SignedOffBy { get; private set; }
    public string SignedOffName { get; private set; } = "";
    public string RegistrationNumber { get; private set; } = "";
    public DateTimeOffset SignedOffAt { get; private set; }
    public string? Notes { get; private set; }
    public string? Error { get; private set; }
    /// <summary>The last reproduce run: every design run re-run from its stored input and compared (plan 7.2).</summary>
    public string? ReproductionJson { get; private set; }
    public DateTimeOffset? ReproducedAt { get; private set; }
    public bool? Reproduced { get; private set; }

    public void Issued(string snapshotKey, string snapshotSha256)
    {
        SnapshotKey = snapshotKey;
        SnapshotSha256 = snapshotSha256;
        Status = RevisionStatus.Issued;
    }

    public void Fail(string error)
    {
        Status = RevisionStatus.Failed;
        Error = error;
    }

    public void RecordReproduction(string json, bool identical, DateTimeOffset now)
    {
        ReproductionJson = json;
        Reproduced = identical;
        ReproducedAt = now;
    }

    public static string LabelFor(int number)
    {
        var label = "";
        for (var n = number; n > 0; n = (n - 1) / 26) label = (char)('A' + (n - 1) % 26) + label;
        return label;
    }
}
