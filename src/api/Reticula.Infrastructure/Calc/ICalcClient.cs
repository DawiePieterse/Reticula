using System.Text.Json;

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

    /// <summary>Parses and validates a layout or map file into WGS84 features.</summary>
    /// <exception cref="CalcRejectedException">The file cannot be read or the options are invalid.</exception>
    Task<CalcImportResult> ImportAsync(CalcImportRequest request, CancellationToken ct = default);

    /// <exception cref="CalcRejectedException">The rules file has no prediction section.</exception>
    Task<PredictionResult> PredictBuildingTypesAsync(string rulesRef, IReadOnlyList<BuildingPredictionInput> buildings, CancellationToken ct = default);

    /// <summary>The observation form for the income and ADMD tool, from the rules file.</summary>
    Task<JsonElement> GetAdmdFormAsync(string rulesRef, CancellationToken ct = default);

    /// <summary>Designs, checks and costs an LV network. The result is stored as given.</summary>
    /// <exception cref="CalcRejectedException">The input cannot be designed (rules without LV data, no routes, missing loads).</exception>
    Task<JsonElement> DesignLvAsync(object request, CancellationToken ct = default);

    /// <summary>Places and sizes transformers and designs each site's LV network and the MV network.</summary>
    Task<JsonElement> DesignMvAsync(object request, CancellationToken ct = default);

    /// <exception cref="CalcRejectedException">Unknown option, bad number or missing rules section.</exception>
    Task<AdmdEstimate> EstimateAdmdAsync(AdmdEstimateRequest request, CancellationToken ct = default);

    Task<AdmdGroup> GroupAdmdAsync(string rulesRef, IReadOnlyList<AdmdGroupLoad> loads, CancellationToken ct = default);
}

public sealed record RulesInfo(string Ref, string Hash, string EffectiveDate);

public sealed class CalcUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
