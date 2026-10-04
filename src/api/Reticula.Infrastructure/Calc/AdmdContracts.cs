using System.Text.Json;

namespace Reticula.Infrastructure.Calc;

public sealed record AdmdEstimateRequest(
    string Rules, string Kind, Dictionary<string, JsonElement>? Observations, string? SpecialLoad, double? OverrideKva, string? OverrideReason,
    string? LoadClass = null);

/// <summary>The load class behind an estimate: its table, published ADMD and whether the engineer chose it.</summary>
public sealed record AdmdLoadClass(string Code, string Description, string Table, string TableSource, double AdmdKva, string ChosenBy);

public sealed record TracedValue(double Value, string Unit, string FormulaId, string Formula, string Clause, string RulesHash, JsonElement Inputs);

/// <param name="Raw">The calc service's full response, kept as the load point's trace.</param>
public sealed record AdmdEstimate(
    string Kind, IReadOnlyList<string> Missing, string? IncomeBand, string? Category, TracedValue AdmdKva,
    double EstimatedKva, bool Overridden, string RulesHash, string Raw, AdmdLoadClass? LoadClass = null);

public sealed record AdmdGroupLoad(string Id, string Kind, double Kva, string? LoadClass = null, int Phases = 1);

public sealed record AdmdGroup(int ResidentialCount, int SpecialCount, TracedValue? DiversityFactor, TracedValue ResidentialKva,
    double SpecialKva, TracedValue TotalKva, string RulesHash, string Method = "admd_factor", int? Phases = null,
    double? ConfidencePct = null, TracedValue? DesignCurrentA = null);
