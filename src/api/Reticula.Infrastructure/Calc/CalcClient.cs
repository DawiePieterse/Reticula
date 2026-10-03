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
        catch (TaskCanceledException) { return false; }
    }

    public async Task<IReadOnlyList<string>> ListRulesAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<List<string>>("/rules", Json, ct) ?? [];

    public async Task<RulesInfo> GetRulesInfoAsync(string authority, string version, CancellationToken ct = default)
        => await http.GetFromJsonAsync<RulesInfo>($"/rules/{authority}/{version}", Json, ct)
           ?? throw new InvalidOperationException("empty rules response");
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
