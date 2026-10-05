using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Reticula.Infrastructure.Calc;

public sealed class CalcClient(HttpClient http) : ICalcClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

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
        var parts = rulesRef.Split('/');
        if (parts.Length != 2) return null;
        using var r = await SendAsync($"/rules/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}", ct);
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
        if (request.ContourInterval is { } interval) form.Add(new StringContent(interval.ToString(System.Globalization.CultureInfo.InvariantCulture)), "contour_interval");

        using var r = await SendAsync(() => http.PostAsync("/geo/import", form, ct), ct);
        return await ReadAsync<CalcImportResult>(r, ct);
    }

    public async Task<PredictionResult> PredictBuildingTypesAsync(string rulesRef, IReadOnlyList<BuildingPredictionInput> buildings, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new { rules = rulesRef, buildings }, options: Json);
        using var r = await SendAsync(() => http.PostAsync("/predict/building-types", content, ct), ct);
        return await ReadAsync<PredictionResult>(r, ct);
    }

    public async Task<JsonElement> ClassifyRooftopsAsync(object request, Stream imagery, string fileName, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent
        {
            { new StreamContent(imagery), "imagery", fileName },
            { new StringContent(JsonSerializer.Serialize(request, Json)), "request" },
        };
        using var r = await SendAsync(() => http.PostAsync("/predict/rooftop", form, ct), ct);
        return await ReadAsync<JsonElement>(r, ct);
    }

    public async Task<JsonElement> DesignLvAsync(object request, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(request, options: Json);
        using var r = await SendAsync(() => http.PostAsync("/calc/lv/design", content, ct), ct);
        return await ReadAsync<JsonElement>(r, ct);
    }

    public async Task<JsonElement> DesignMvAsync(object request, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(request, options: Json);
        using var r = await SendAsync(() => http.PostAsync("/calc/mv/design", content, ct), ct);
        return await ReadAsync<JsonElement>(r, ct);
    }

    public async Task<IReadOnlyList<string>> ListRatesAsync(CancellationToken ct = default)
    {
        using var r = await SendAsync("/rates", ct);
        return await ReadAsync<List<string>>(r, ct);
    }

    public async Task<JsonElement?> GetRatesAsync(string rateRef, CancellationToken ct = default)
    {
        using var r = await SendAsync($"/rates/{string.Join('/', rateRef.Split('/').Select(Uri.EscapeDataString))}", ct);
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        return await ReadAsync<JsonElement>(r, ct);
    }

    public async Task<JsonElement> RenderDocumentsAsync(object request, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(request, options: Json);
        using var r = await SendAsync(() => http.PostAsync("/docs/render", content, ct), ct);
        return await ReadAsync<JsonElement>(r, ct);
    }

    public async Task<JsonElement> OptimiseLvAsync(object request, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(request, options: Json);
        using var r = await SendAsync(() => http.PostAsync("/calc/lv/optimise", content, ct), ct);
        return await ReadAsync<JsonElement>(r, ct);
    }

    public async Task<JsonElement> StudyBulkAsync(object request, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(request, options: Json);
        using var r = await SendAsync(() => http.PostAsync("/calc/bulk/study", content, ct), ct);
        return await ReadAsync<JsonElement>(r, ct);
    }

    public async Task<JsonElement> GetAdmdFormAsync(string rulesRef, CancellationToken ct = default)
    {
        var parts = rulesRef.Split('/');
        using var r = await SendAsync($"/calc/admd/form/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts.ElementAtOrDefault(1) ?? "")}", ct);
        return await ReadAsync<JsonElement>(r, ct);
    }

    public async Task<AdmdEstimate> EstimateAdmdAsync(AdmdEstimateRequest request, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(request, options: Json);
        using var r = await SendAsync(() => http.PostAsync("/calc/admd/estimate", content, ct), ct);
        var raw = await ReadAsync<JsonElement>(r, ct);
        var typed = raw.Deserialize<AdmdEstimate>(Json) ?? throw new CalcUnavailableException("Calc service returned an empty body.");
        return typed with { Raw = raw.GetRawText() };
    }

    public async Task<AdmdGroup> GroupAdmdAsync(string rulesRef, IReadOnlyList<AdmdGroupLoad> loads, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new { rules = rulesRef, loads }, options: Json);
        using var r = await SendAsync(() => http.PostAsync("/calc/admd/group", content, ct), ct);
        return await ReadAsync<AdmdGroup>(r, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage r, CancellationToken ct)
    {
        if (r.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.RequestEntityTooLarge)
            throw new CalcRejectedException(await DetailAsync(r, ct));
        if (!r.IsSuccessStatusCode)
            throw new CalcUnavailableException($"Calc service returned {(int)r.StatusCode}.");
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
            // Imports of large layouts and option searches (background jobs) take a while; health checks pass their own short token.
            c.Timeout = TimeSpan.FromMinutes(15);
        });
        return services;
    }
}
