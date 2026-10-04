using NetTopologySuite.Geometries;

namespace Reticula.Domain.Field;

public static class InspectionActions
{
    public const string Confirm = "confirm";
    public const string Correct = "correct";
    public const string NotPresent = "not_present";
    public const string NewBuilding = "new_building";
    public const string Candidate = "candidate";
    public const string Load = "load";
    public static readonly IReadOnlyList<string> BuildingActions = [Confirm, Correct, NotPresent];
}

/// <summary>
/// One thing an inspector recorded on site: what, where (GPS), when (device clock) and notes.
/// Ids are generated on the device so a record synced twice is stored once.
/// </summary>
public sealed class Inspection
{
    private Inspection() { } // EF

    public Inspection(Guid id, Guid projectId, string action, Guid? buildingId, Guid? candidateId, string? value,
        Point? position, double? accuracyM, DateTimeOffset capturedAt, string? notes, Guid inspectorId, DateTimeOffset recordedAt)
    {
        Id = id;
        ProjectId = projectId;
        Action = action;
        BuildingId = buildingId;
        CandidateId = candidateId;
        Value = value;
        Position = position;
        AccuracyM = accuracyM;
        CapturedAt = capturedAt;
        Notes = notes;
        InspectorId = inspectorId;
        RecordedAt = recordedAt;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Action { get; private set; } = "";
    public Guid? BuildingId { get; private set; }
    public Guid? CandidateId { get; private set; }

    /// <summary>For building actions, the type confirmed or corrected to.</summary>
    public string? Value { get; private set; }

    public Point? Position { get; private set; }
    public double? AccuracyM { get; private set; }

    /// <summary>When the inspector recorded it, by the device clock.</summary>
    public DateTimeOffset CapturedAt { get; private set; }

    /// <summary>When the server received it.</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    public string? Notes { get; private set; }
    public Guid InspectorId { get; private set; }
}
