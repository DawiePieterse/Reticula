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

    /// <exception cref="CalcRejectedException">Unknown option, bad number or missing rules section.</exception>
    Task<AdmdEstimate> EstimateAdmdAsync(AdmdEstimateRequest request, CancellationToken ct = default);

    /// <summary>The basemap for a box as a PMTiles archive, cut from the calc service's configured map source.</summary>
    /// <exception cref="CalcRejectedException">No map source is configured, it cannot be read, or the box is too large.</exception>
    Task<MapExtract> ExtractMapAsync(double minLon, double minLat, double maxLon, double maxLat, CancellationToken ct = default);

    Task<AdmdGroup> GroupAdmdAsync(string rulesRef, IReadOnlyList<AdmdGroupLoad> loads, CancellationToken ct = default);
}

public sealed record RulesInfo(string Ref, string Hash, string EffectiveDate);

public sealed class CalcUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
