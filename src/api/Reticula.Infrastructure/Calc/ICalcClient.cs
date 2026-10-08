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

    /// <summary>Buildings or roads for the area, fetched from OpenStreetMap by the calc service and checked like a file.</summary>
    /// <exception cref="CalcRejectedException">OpenStreetMap could not be reached, or the area is too large.</exception>
    Task<CalcImportResult> ImportOsmAsync(string kind, string areaGeoJson, CancellationToken ct = default);

    /// <exception cref="CalcRejectedException">The rules file has no prediction section.</exception>
    Task<PredictionResult> PredictBuildingTypesAsync(string rulesRef, IReadOnlyList<BuildingPredictionInput> buildings, CancellationToken ct = default);

    /// <summary>The observation form for the income and ADMD tool, from the rules file.</summary>
    Task<JsonElement> GetAdmdFormAsync(string rulesRef, CancellationToken ct = default);

    /// <exception cref="CalcRejectedException">Unknown option, bad number or missing rules section.</exception>
    Task<AdmdEstimate> EstimateAdmdAsync(AdmdEstimateRequest request, CancellationToken ct = default);

    /// <summary>The basemap for a box as a PMTiles archive, cut from the calc service's configured map source.</summary>
    /// <exception cref="CalcRejectedException">No map source is configured, it cannot be read, or the box is too large.</exception>
    Task<MapExtract> ExtractMapAsync(double minLon, double minLat, double maxLon, double maxLat, CancellationToken ct = default);

    /// <summary>Joins the LV routes and sites marked in the field into a node-branch network and checks it is radial.</summary>
    /// <exception cref="CalcRejectedException">The rules file has no lv_network section.</exception>
    Task<CalcLvNetwork> BuildLvNetworkAsync(string rulesRef, IReadOnlyList<LvCandidate> candidates, CancellationToken ct = default);

    /// <summary>Connects each building's load to the LV network and spreads single-phase loads over the phases of their feeder.</summary>
    /// <exception cref="CalcRejectedException">The rules file has no lv_loads section.</exception>
    Task<CalcLvLoads> AllocateLvLoadsAsync(string rulesRef, CalcLvNetwork network, IReadOnlyList<LvLoadIn> loads, CancellationToken ct = default);

    /// <summary>The rules file's conductor library; null when the rules file does not exist.</summary>
    Task<CalcConductorLibrary?> GetConductorsAsync(string rulesRef, CancellationToken ct = default);

    /// <summary>Voltage drop, thermal loading and fault level of the connected LV network.</summary>
    /// <exception cref="CalcRejectedException">The rules file has no lv_design section.</exception>
    Task<CalcLvAnalysis> AnalyseLvAsync(string rulesRef, CalcLvNetwork network, IReadOnlyList<LvLoadAt> loads, CancellationToken ct = default);

    /// <summary>Pre-design (ADR 0010): proposes transformer sites, the loads each feeds, LV routes along roads and an MV route.</summary>
    /// <exception cref="CalcRejectedException">The rules file has no mv_design section.</exception>
    Task<CalcPlacement> PlaceLvAsync(string rulesRef, IReadOnlyList<PlacementRoad> roads, IReadOnlyList<PlacementLoad> loads, double[]? connectionPoint,
        CancellationToken ct = default);

    /// <summary>
    /// The whole design (plans 2.5 to 5.1) from a design request in the calc service's JSON; returns the Design as the calc service's JSON.
    /// The API keeps it as given and never recomputes from it.
    /// </summary>
    /// <exception cref="CalcRejectedException">The request is invalid or the rules file lacks a section the design needs.</exception>
    Task<string> RunDesignAsync(string requestJson, CancellationToken ct = default);

    /// <summary>Three options by local search and siting (plans 5.2 to 5.4), each a full design with the request that gives it.</summary>
    Task<string> OptimiseDesignAsync(string requestJson, CancellationToken ct = default);

    /// <summary>The calc service's indicative rate library, as JSON.</summary>
    Task<string> GetDefaultRatesAsync(CancellationToken ct = default);

    /// <summary>One design document (Phase 6) from {meta, design, rows?, totals?, sections?}.</summary>
    /// <exception cref="CalcRejectedException">Unknown kind, or the body does not fit it.</exception>
    Task<CalcFile> RenderDocumentAsync(string kind, string bodyJson, CancellationToken ct = default);

    /// <summary>The submission pack (plan 6.6) from {meta, design, files}.</summary>
    Task<CalcFile> PackDocumentsAsync(string bodyJson, CancellationToken ct = default);

    Task<AdmdGroup> GroupAdmdAsync(string rulesRef, IReadOnlyList<AdmdGroupLoad> loads, CancellationToken ct = default);
}

public sealed record RulesInfo(string Ref, string Hash, string EffectiveDate);

public sealed class CalcUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
