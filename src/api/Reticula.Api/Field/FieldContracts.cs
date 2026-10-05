using System.Text.Json;
using Reticula.Infrastructure.Geo;

namespace Reticula.Api.Field;

public sealed record PositionDto(double Lon, double Lat, double? AccuracyM);

/// <param name="InspectionId">Generated on the device; a repeat with the same id is ignored.</param>
/// <param name="Version">The building version the inspector saw. A mismatch returns 409 with the current state.</param>
public sealed record BuildingInspectionRequest(
    Guid InspectionId, string Action, string? Type, PositionDto? Position, DateTimeOffset CapturedAt, string? Notes, uint? Version);

public sealed record NewBuildingRequest(Guid Id, Guid InspectionId, string Type, PositionDto Position, DateTimeOffset CapturedAt, string? Notes);

public sealed record BuildingFieldDto(
    Guid Id, string Status, string PredictedType, string? ConfirmedType, string EffectiveType, double Confidence,
    string? Erf, PointDto Location, DateTimeOffset? InspectedAt, uint Version);

/// <param name="Version">The candidate version the inspector saw; null to create. A mismatch returns 409 with the current state.</param>
/// <param name="OpId">Generated on the device; a repeat with the same id is ignored and returns the current state.</param>
public sealed record CandidateRequest(string Kind, GeometryInput Geometry, string? Notes, uint? Version, PositionDto? Position, DateTimeOffset? CapturedAt,
    Guid? OpId = null);

public sealed record CandidateProps(string Kind, string? Notes, DateTimeOffset CreatedAt, uint Version);

/// <param name="Version">The load point version the inspector saw; null when the building had no load. A mismatch returns 409 with the current state.</param>
/// <param name="LoadClass">A class the engineer chooses instead of the score; null to use the score.</param>
/// <param name="OpId">Generated on the device; a repeat with the same id is ignored and returns the current state.</param>
/// <param name="CapturedAt">When the load was recorded, by the device clock.</param>
public sealed record LoadRequest(
    string Kind, Dictionary<string, JsonElement>? Observations, string? SpecialLoad, double? OverrideKva, string? OverrideReason, uint? Version,
    string? LoadClass = null, Guid? OpId = null, DateTimeOffset? CapturedAt = null);

public sealed record LoadPointDto(
    Guid Id, Guid BuildingId, string Kind, string? SpecialLoad, JsonElement Observations, string? ClassOverride, string? IncomeBand, string? Category,
    double EstimatedKva, double Kva, bool Overridden, string? OverrideReason, IReadOnlyList<string> Missing, string Status,
    DateTimeOffset UpdatedAt, uint Version);

public sealed record AssumptionDto(Guid Id, string SubjectType, Guid SubjectId, string Code, string Text, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? ClearedAt, string? ClearNote);

public sealed record ClearAssumptionRequest(string? Note);

public sealed record PhotoDto(Guid Id, Guid? BuildingId, Guid? CandidateId, string ContentType, long SizeBytes, DateTimeOffset CapturedAt);

public sealed record FieldProgress(
    int Buildings, int Confirmed, int NotPresent, int Added, int Outstanding, int OutstandingLowConfidence,
    int LoadsEstimated, int LoadsConfirmed, int BuildingsWithoutLoad, int AssumptionsOpen,
    IReadOnlyDictionary<string, int> Candidates);

public sealed record LoadScheduleRow(
    string? Erf, Guid BuildingId, string BuildingType, string BuildingStatus, string? LoadKind, string? Category, string? IncomeBand,
    double? Kva, double? EstimatedKva, bool Overridden, string? OverrideReason, string? LoadStatus);

public sealed record LoadScheduleTotals(int ResidentialCount, int SpecialCount, double? DiversityFactor, double ResidentialKva,
    double SpecialKva, double TotalKva, string Formula, string Clause, string Method, int? Phases, double? ConfidencePct, double? DesignCurrentA);

public sealed record LoadSchedule(string Project, string RulesRef, string RulesHash, DateTimeOffset GeneratedAt,
    IReadOnlyList<LoadScheduleRow> Rows, LoadScheduleTotals? Totals);
