namespace Reticula.Domain.Field;

/// <summary>A site photo. The bytes live in the file store under <see cref="StorageKey"/>.</summary>
public sealed class Photo
{
    private Photo() { } // EF

    public Photo(Guid id, Guid projectId, Guid? buildingId, Guid? candidateId, Guid? inspectionId, string contentType,
        long sizeBytes, string sha256, string storageKey, DateTimeOffset capturedAt, Guid uploadedBy, DateTimeOffset uploadedAt)
    {
        Id = id;
        ProjectId = projectId;
        BuildingId = buildingId;
        CandidateId = candidateId;
        InspectionId = inspectionId;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        StorageKey = storageKey;
        CapturedAt = capturedAt;
        UploadedBy = uploadedBy;
        UploadedAt = uploadedAt;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid? BuildingId { get; private set; }
    public Guid? CandidateId { get; private set; }
    public Guid? InspectionId { get; private set; }
    public string ContentType { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = "";
    public string StorageKey { get; private set; } = "";
    public DateTimeOffset CapturedAt { get; private set; }
    public Guid UploadedBy { get; private set; }
    public DateTimeOffset UploadedAt { get; private set; }
}
