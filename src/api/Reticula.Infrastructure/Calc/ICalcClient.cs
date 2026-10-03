namespace Reticula.Infrastructure.Calc;

/// <summary>Gateway to the Python calculation service. No engineering number is computed on the .NET side.</summary>
public interface ICalcClient
{
    Task<bool> IsHealthyAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListRulesAsync(CancellationToken ct = default);
    Task<RulesInfo> GetRulesInfoAsync(string authority, string version, CancellationToken ct = default);
}

public sealed record RulesInfo(string Ref, string Hash, string EffectiveDate);
