using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Reticula.Infrastructure.Calc;

public sealed class CalcClient(HttpClient http) : ICalcClient
{
    private static readonly JsonSerializerOptions Json = CalcJson.Options;

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await http.GetAsync("/health", ct);
            return r.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    public async Task<IReadOnlyList<string>> ListRulesAsync(CancellationToken ct = default)
    {
        using var r = await SendAsync("/rules", ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<List<string>>(Json, ct) ?? [];
    }

    public async Task<RulesInfo?> GetRulesInfoAsync(string rulesRef, CancellationToken ct = default)
    {
        if (RulesPath(rulesRef) is not { } path) return null;
        using var r = await SendAsync(path, ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        if (!r.IsSuccessStatusCode) throw new CalcUnavailableException($"Calc service returned {(int)r.StatusCode} for rules {rulesRef}.");
        return await r.Content.ReadFromJsonAsync<RulesInfo>(Json, ct);
    }

    public async Task<CalcImportResult> ImportAsync(CalcImportRequest request, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(request.File);
        form.Add(file, "file", request.FileName);
        form.Add(new StringContent(request.Kind), "kind");
        if (!string.IsNullOrWhiteSpace(request.SourceCrs)) form.Add(new StringContent(request.SourceCrs), "source_crs");
        if (!string.IsNullOrWhiteSpace(request.Layer)) form.Add(new StringContent(request.Layer), "layer");
        if (!string.IsNullOrWhiteSpace(request.AreaGeoJson)) form.Add(new StringContent(request.AreaGeoJson), "area");

        using var r = await SendAsync(() => http.PostAsync("/geo/import", form, ct), ct);
        return await ReadAsync<CalcImportResult>(r, ct);
    }

    public async Task<CalcImportResult> ImportOsmAsync(string kind, string areaGeoJson, CancellationToken ct = default)
    {
        return await PostAsync<CalcImportResult>("/geo/osm", new { kind, area = JsonDocument.Parse(areaGeoJson).RootElement }, ct);
    }

    public async Task<PredictionResult> PredictBuildingTypesAsync(string rulesRef, IReadOnlyList<BuildingPredictionInput> buildings, CancellationToken ct = default)
    {
        return await PostAsync<PredictionResult>("/predict/building-types", new { rules = rulesRef, buildings }, ct);
    }

    public async Task<JsonElement> GetAdmdFormAsync(string rulesRef, CancellationToken ct = default)
    {
        var path = RulesPath(rulesRef) ?? throw new CalcRejectedException($"Rules reference '{rulesRef}' is not authority/version.");
        using var r = await SendAsync($"/calc/admd/form{path["/rules".Length..]}", ct);
        return await ReadAsync<JsonElement>(r, ct);
    }

    public async Task<AdmdEstimate> EstimateAdmdAsync(AdmdEstimateRequest request, CancellationToken ct = default)
    {
        var raw = await PostAsync<JsonElement>("/calc/admd/estimate", request, ct);
        var typed = raw.Deserialize<AdmdEstimate>(Json) ?? throw new CalcUnavailableException("Calc service returned an empty body.");
        return typed with { Raw = raw.GetRawText() };
    }

    public async Task<AdmdGroup> GroupAdmdAsync(string rulesRef, IReadOnlyList<AdmdGroupLoad> loads, CancellationToken ct = default)
    {
        return await PostAsync<AdmdGroup>("/calc/admd/group", new { rules = rulesRef, loads }, ct);
    }

    public async Task<CalcLvNetwork> BuildLvNetworkAsync(string rulesRef, IReadOnlyList<LvCandidate> candidates, CancellationToken ct = default)
    {
        return await PostAsync<CalcLvNetwork>("/calc/lv/network", new { rules = rulesRef, candidates }, ct);
    }

    public async Task<CalcLvLoads> AllocateLvLoadsAsync(string rulesRef, CalcLvNetwork network, IReadOnlyList<LvLoadIn> loads, CancellationToken ct = default)
    {
        return await PostAsync<CalcLvLoads>("/calc/lv/loads", new { rules = rulesRef, network, loads }, ct);
    }

    public async Task<CalcConductorLibrary?> GetConductorsAsync(string rulesRef, CancellationToken ct = default)
    {
        if (RulesPath(rulesRef) is not { } path) return null;
        using var r = await SendAsync($"{path}/conductors", ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadAsync<CalcConductorLibrary>(r, ct);
    }

    public async Task<CalcLvAnalysis> AnalyseLvAsync(string rulesRef, CalcLvNetwork network, IReadOnlyList<LvLoadAt> loads, CancellationToken ct = default)
    {
        return await PostAsync<CalcLvAnalysis>("/calc/lv/analyse", new { rules = rulesRef, network, loads }, ct);
    }

    public async Task<CalcPlacement> PlaceLvAsync(string rulesRef, IReadOnlyList<PlacementRoad> roads, IReadOnlyList<PlacementLoad> loads, double[]? connectionPoint,
        CancellationToken ct = default) =>
        await PostAsync<CalcPlacement>("/calc/lv/placement", new { rules = rulesRef, roads, loads, connection_point = connectionPoint }, ct);

    public async Task<MapExtract> ExtractMapAsync(double minLon, double minLat, double maxLon, double maxLat, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new { bbox = new[] { minLon, minLat, maxLon, maxLat } }, options: Json);
        using var r = await SendAsync(() => http.PostAsync("/maps/extract", content, ct), ct);
        await EnsureAcceptedAsync(r, ct);
        static string Header(HttpResponseMessage r, string name) => r.Headers.TryGetValues(name, out var v) ? v.First() : "";
        return new MapExtract(
            await r.Content.ReadAsByteArrayAsync(ct),
            int.TryParse(Header(r, "X-Tile-Count"), out var tiles) ? tiles : 0,
            int.TryParse(Header(r, "X-Max-Zoom"), out var zoom) ? zoom : 0,
            Header(r, "X-Map-Source"));
    }

    public Task<string> RunDesignAsync(string requestJson, CancellationToken ct = default) => PostRawAsync("/calc/design/run", requestJson, ct);

    public Task<string> OptimiseDesignAsync(string requestJson, CancellationToken ct = default) => PostRawAsync("/calc/design/optimise", requestJson, ct);

    public async Task<string> GetDefaultRatesAsync(CancellationToken ct = default)
    {
        using var r = await SendAsync("/rates/default", ct);
        await EnsureAcceptedAsync(r, ct);
        return await r.Content.ReadAsStringAsync(ct);
    }

    public Task<CalcFile> RenderDocumentAsync(string kind, string bodyJson, CancellationToken ct = default) =>
        PostFileAsync($"/calc/documents/{Uri.EscapeDataString(kind)}", bodyJson, ct);

    public Task<CalcFile> PackDocumentsAsync(string bodyJson, CancellationToken ct = default) => PostFileAsync("/calc/documents/pack", bodyJson, ct);

    private async Task<string> PostRawAsync(string path, string json, CancellationToken ct)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var r = await SendAsync(() => http.PostAsync(path, content, ct), ct);
        await EnsureAcceptedAsync(r, ct);
        return await r.Content.ReadAsStringAsync(ct);
    }

    private async Task<CalcFile> PostFileAsync(string path, string json, CancellationToken ct)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var r = await SendAsync(() => http.PostAsync(path, content, ct), ct);
        if (r.StatusCode == HttpStatusCode.NotFound) throw new CalcRejectedException($"The calc service cannot render '{path}'.");
        await EnsureAcceptedAsync(r, ct);
        var name = r.Content.Headers.ContentDisposition?.FileNameStar ?? r.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "document";
        return new CalcFile(await r.Content.ReadAsByteArrayAsync(ct), r.Content.Headers.ContentType?.MediaType ?? "application/octet-stream", name);
    }

    /// <summary>"/rules/{authority}/{version}" for an authority/version reference, or null when the reference is malformed.</summary>
    private static string? RulesPath(string rulesRef)
    {
        var parts = rulesRef.Split('/');
        return parts.Length == 2 ? $"/rules/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}" : null;
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var content = JsonContent.Create(body, options: Json);
        using var r = await SendAsync(() => http.PostAsync(path, content, ct), ct);
        return await ReadAsync<T>(r, ct);
    }

    /// <summary>A 422 or 413 is the calc service rejecting the request; any other failure means it is unavailable.</summary>
    private static async Task EnsureAcceptedAsync(HttpResponseMessage r, CancellationToken ct)
    {
        if (r.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.RequestEntityTooLarge)
            throw new CalcRejectedException(await DetailAsync(r, ct));
        if (!r.IsSuccessStatusCode)
            throw new CalcUnavailableException($"Calc service returned {(int)r.StatusCode}.");
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage r, CancellationToken ct)
    {
        await EnsureAcceptedAsync(r, ct);
        return await r.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new CalcUnavailableException("Calc service returned an empty body.");
    }

    private static async Task<string> DetailAsync(HttpResponseMessage r, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("detail", out var d))
                return d.ValueKind == JsonValueKind.String ? d.GetString()! : d.ToString();
        }
        catch (JsonException) { }
        return $"Calc service rejected the request ({(int)r.StatusCode}).";
    }

    private Task<HttpResponseMessage> SendAsync(string path, CancellationToken ct) => SendAsync(() => http.GetAsync(path, ct), ct);

    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException e) { throw new CalcUnavailableException("Calc service unreachable.", e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new CalcUnavailableException("Calc service timed out.", e); }
    }
}

public static class CalcServiceCollectionExtensions
{
    public static IServiceCollection AddCalcClient(this IServiceCollection services, IConfiguration config)
    {
        var baseUrl = config["Calc:BaseUrl"] ?? "http://localhost:8001";
        services.AddHttpClient<ICalcClient, CalcClient>(c =>
        {
            c.BaseAddress = new Uri(baseUrl);
            // Imports of large layouts and design optimisation take a while; health checks pass their own short token.
            c.Timeout = TimeSpan.FromSeconds(config.GetValue("Calc:TimeoutSeconds", 900));
        });
        return services;
    }
}
