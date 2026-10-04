namespace Reticula.Infrastructure.Calc;

/// <summary>Gateway to the Python calculation service. No engineering number is computed on the .NET side.</summary>
public interface ICalcClient
{
    Task<bool> IsHealthyAsync(CancellationToken ct = default);

    /// <exception cref="CalcUnavailableException">The calc service could not be reached.</exception>
    Task<IReadOnlyList<string>> ListRulesAsync(CancellationToken ct = default);

    /// <summary>Returns null when the rules file does not exist.</summary>
    /// <exception cref="CalcUnavailableException">The calc service could not be reached.</exception>
    Task<RulesInfo?> GetRulesInfoAsync(string rulesRef, CancellationToken ct = default);
}

public sealed record RulesInfo(string Ref, string Hash, string EffectiveDate);

public sealed class CalcUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
