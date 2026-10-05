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
