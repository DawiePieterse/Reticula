using System.Text.Json;

namespace Reticula.Infrastructure.Calc;

public sealed record AdmdEstimateRequest(
    string Rules, string Kind, Dictionary<string, JsonElement>? Observations, string? SpecialLoad, double? OverrideKva, string? OverrideReason);

public sealed record TracedValue(double Value, string Unit, string FormulaId, string Formula, string Clause, string RulesHash, JsonElement Inputs);

/// <param name="Raw">The calc service's full response, kept as the load point's trace.</param>
public sealed record AdmdEstimate(
    string Kind, IReadOnlyList<string> Missing, string? IncomeBand, string? Category, TracedValue AdmdKva,
    double EstimatedKva, bool Overridden, string RulesHash, string Raw);

public sealed record AdmdGroupLoad(string Id, string Kind, double Kva);

public sealed record AdmdGroup(int ResidentialCount, int SpecialCount, TracedValue? DiversityFactor, TracedValue ResidentialKva,
    double SpecialKva, TracedValue TotalKva, string RulesHash);
