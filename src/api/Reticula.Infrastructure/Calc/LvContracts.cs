using System.Text.Json;

namespace Reticula.Infrastructure.Calc;

/// <summary>A site or route marked in the field, with GeoJSON geometry, as the calc service's LV network build takes it.</summary>
public sealed record LvCandidate(string Id, string Kind, JsonElement Geometry);

public sealed record CalcLvNode(string Id, string Kind, double[] Coordinates, string? Label, string? CandidateId, string? Feeder, double? DistanceM);

public sealed record CalcLvBranch(string Id, string Kind, string FromNode, string ToNode, double[][] Coordinates, double LengthM,
    string? CandidateId, string? Feeder);

public sealed record LvFeeder(string Id, string Source, int Branches, double LengthM, int Ends, double FarthestM);

/// <param name="At">Where to look, as [lon, lat] positions.</param>
public sealed record LvIssue(string Severity, string Code, string Message, int Count, IReadOnlyList<string> Samples, IReadOnlyList<double[]> At);

public sealed record LvSummary(int Routes, int Sources, int SourcesConnected, int Poles, int PolesPlaced, int Feeders, int Nodes, int Branches,
    double RouteLengthM, double UnfedLengthM);

public sealed record CalcLvNetwork(string RulesRef, string RulesHash, string Clause, IReadOnlyList<CalcLvNode> Nodes, IReadOnlyList<CalcLvBranch> Branches,
    IReadOnlyList<LvFeeder> Feeders, IReadOnlyList<LvIssue> Issues, LvSummary Summary);

/// <param name="Id">The load point, or the building when it has no load estimate yet.</param>
/// <param name="Kva">Design kVA; null when the building has no load estimate yet.</param>
public sealed record LvLoadIn(string Id, string BuildingId, string? Label, double[] Coordinates, double? Kva, string Kind);

/// <param name="Box">The service distribution box on a pole, e.g. P3-1; null for three-phase loads and with nearest_point.</param>
public sealed record CalcLvAllocation(string LoadId, string BuildingId, string? Label, string Kind, double Kva, string Branch, string? Node,
    double OffsetM, double[] At, double ServiceM, string? Box, string? Feeder, double? DistanceM, string? Phase);

/// <summary>A service distribution box on a pole: up to the rules file's number of loads, all on one phase.</summary>
public sealed record LvBox(string Id, string Pole, string? Feeder, string? Phase, int Loads, double Kva, double? DistanceM);

public sealed record LvPhaseLoad(int Customers, double Kva, int Boxes);

public sealed record LvFeederPhases(string Feeder, IReadOnlyDictionary<string, LvPhaseLoad> Phases, int ThreePhase, double UnbalancePct);

public sealed record LvLoadSummary(int Loads, int Allocated, int Unallocated, int Unestimated, int OnFeeders, int ThreePhase, int Boxes,
    double AllocatedKva, double LongestServiceM);

/// <param name="Attach">pole_boxes or nearest_point, from the rules file.</param>
public sealed record CalcLvLoads(string RulesRef, string RulesHash, string Clause, string Attach, IReadOnlyList<CalcLvAllocation> Allocations,
    IReadOnlyList<LvBox> Boxes, IReadOnlyList<LvFeederPhases> Feeders, IReadOnlyList<LvIssue> Issues, LvLoadSummary Summary);

/// <param name="RatingsA">Continuous rating by installation: ground, pipe, air.</param>
/// <param name="OneSecondKa">Short-circuit withstand for 1 s, from FaultK.</param>
/// <param name="Placeholder">Fields whose values are placeholders, not yet from the governing standard.</param>
/// <param name="ROhmPerKm">DC resistance at 20 °C.</param>
/// <param name="RAcOhmPerKm">AC resistance at RAcTempC, where the source gives it.</param>
public sealed record CalcConductor(string Code, string Description, string Kind, string? Material, double? SizeMm2, int? Cores,
    IReadOnlyList<string> Uses, double ROhmPerKm, double XOhmPerKm, double RatingA, IReadOnlyDictionary<string, double> RatingsA,
    double? FaultK, double? OneSecondKa, IReadOnlyList<string> Placeholder, string Clause, string RatingClause, string Index,
    double? RAcOhmPerKm = null, double? RAcTempC = null);

public sealed record CalcConductorLibrary(string RulesRef, string RulesHash, IReadOnlyList<CalcConductor> Conductors);
