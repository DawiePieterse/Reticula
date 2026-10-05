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

/// <summary>A connected load as the LV checks take it (plan 2.4).</summary>
/// <param name="LoadClass">The Herman-Beta load class of a residential load; null takes it at its ADMD.</param>
public sealed record LvLoadAt(string LoadId, string Branch, double OffsetM, string? Phase, double Kva, string Kind, string? LoadClass, string? Label);

/// <param name="Id">A network node id, or the load point id at a service connection.</param>
public sealed record LvPointResult(string Id, string Kind, string? Feeder, double DistanceM, IReadOnlyDictionary<string, double> DropPct,
    double WorstPct, double FaultA, bool Passes);

public sealed record LvBranchResult(string Id, string? Feeder, string Conductor, double RatingA, IReadOnlyDictionary<string, double> CurrentA,
    double UtilisationPct, bool Passes);

public sealed record LvFeederResult(string Feeder, double MaxDropPct, string MaxDropAt, double MaxUtilisationPct, string MaxUtilisationBranch,
    double MinFaultA, string MinFaultAt, bool Passes);

/// <summary>Voltage drop, thermal loading and fault level of the connected LV network (plan 2.4).</summary>
/// <param name="Placeholders">Inputs that are placeholders, in words; results that use them are not fit to submit.</param>
public sealed record CalcLvAnalysis(string RulesRef, string RulesHash, string Clause, double LimitPct, double PhaseVoltageV, double ConfidencePct,
    IReadOnlyList<LvPointResult> Points, IReadOnlyList<LvBranchResult> Branches, IReadOnlyList<LvFeederResult> Feeders, IReadOnlyList<LvIssue> Issues,
    TracedValue? WorstDrop, TracedValue? WorstCurrent, TracedValue? LowestFault, IReadOnlyList<string> Placeholders);

/// <param name="BendM">How far the route strays from the straight line between the supports.</param>
/// <param name="TensionKn">The strain section's greatest tension, cold or wind; null without mechanical data.</param>
public sealed record LvSpanResult(string Id, string FromNode, string ToNode, string FromLabel, string ToLabel, IReadOnlyList<string> Branches,
    string? Feeder, string Conductor, double LengthM, double BendM, string? Section, double? SagM, double? ClearanceM, double? TensionKn, bool Passes,
    double[][] Coordinates);

/// <param name="Marked">A marked pole or source; false where a pole is needed but not marked.</param>
/// <param name="Role">terminal, intermediate, angle, strain or junction.</param>
/// <param name="PoleClass">Null at a source, or where no load could be worked out.</param>
public sealed record LvSupportResult(string Id, string Label, string Kind, bool Marked, string Role, int Spans, double? DeviationDeg, double? LoadKn,
    string? GoverningCase, string? PoleClass, bool Stay, double? StayTensionKn, bool Passes, double[] Coordinates);

/// <param name="TensionKn">Horizontal tension in each loading case: everyday, hot, cold, wind.</param>
/// <param name="Governing">What set the everyday tension: everyday, or max_tension where the cold or wind case would exceed it.</param>
public sealed record LvSectionResult(string Id, IReadOnlyList<string> Spans, string Conductor, double RulingSpanM,
    IReadOnlyDictionary<string, double> TensionKn, string Governing, double MaxPullKn, bool Passes);

public sealed record LvOverheadSummary(int Spans, double LongestSpanM, int Sections, int Supports, int PolesNeeded, int Stays,
    IReadOnlyDictionary<string, int> PoleClasses);

/// <summary>Overhead line checks of the LV network (plan 2.5): spans, sag and tension, ground clearance, pole loads and stays.</summary>
public sealed record CalcLvOverhead(string RulesRef, string RulesHash, string Clause, LvOverheadSummary Summary, IReadOnlyList<LvSpanResult> Spans,
    IReadOnlyList<LvSupportResult> Supports, IReadOnlyList<LvSectionResult> Sections, IReadOnlyList<LvIssue> Issues,
    TracedValue? LowestClearance, TracedValue? HighestPoleLoad, IReadOnlyList<string> Placeholders);
