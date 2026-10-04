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

    private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken ct)
    {
        try
        {
            return await http.GetAsync(path, ct);
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
            c.Timeout = TimeSpan.FromSeconds(10);
        });
        return services;
    }
}
