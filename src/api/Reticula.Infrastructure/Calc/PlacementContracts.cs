using System.Text.Json;

namespace Reticula.Infrastructure.Calc;

public sealed record PlacementRoad(string Id, IReadOnlyList<double[]> Coordinates);

/// <param name="Kva">ADMD, or the special load's kVA.</param>
public sealed record PlacementLoad(string Id, double[] Coordinates, double Kva, string Kind, string? LoadClass, string? Label);

public sealed record CalcPlacementTransformer(string Id, double[] Coordinates, double RatingKva, int Loads, double DemandKva, double UtilisationPct,
    int Feeders, double LvLengthM);

public sealed record CalcPlacementAssignment(string LoadId, string Transformer, double RoadM, double ServiceM);

public sealed record CalcPlacementRoute(string? Transformer, IReadOnlyList<double[]> Coordinates, double LengthM);

/// <summary>A proposed site or route in the shape of a field candidate.</summary>
public sealed record CalcPlacementCandidate(string Kind, JsonElement Geometry, string? Label);

public sealed record CalcPlacement(
    string RulesRef, string RulesHash, string Clause,
    IReadOnlyList<CalcPlacementTransformer> Transformers, IReadOnlyList<CalcPlacementAssignment> Assignments,
    IReadOnlyList<CalcPlacementRoute> LvRoutes, IReadOnlyList<CalcPlacementRoute> MvRoutes,
    IReadOnlyList<CalcPlacementCandidate> Candidates,
    JsonElement Cost, JsonElement? WorstDemand, IReadOnlyList<LvIssue> Issues, IReadOnlyList<string> Placeholders);
