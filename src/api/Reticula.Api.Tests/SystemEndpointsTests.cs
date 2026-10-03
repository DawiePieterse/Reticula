using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Reticula.Api;
using Reticula.Infrastructure.Calc;

namespace Reticula.Api.Tests;

public class SystemEndpointsTests
{
    private sealed class FakeCalc(bool healthy) : ICalcClient
    {
        public Task<bool> IsHealthyAsync(CancellationToken ct = default) => Task.FromResult(healthy);
        public Task<IReadOnlyList<string>> ListRulesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(["eskom/0.1.0"]);
        public Task<RulesInfo> GetRulesInfoAsync(string a, string v, CancellationToken ct = default)
            => Task.FromResult(new RulesInfo($"{a}/{v}", "abc", "2026-10-03"));
    }

    private static HttpClient Client(bool calcHealthy) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<ICalcClient>(new FakeCalc(calcHealthy)))))
            .CreateClient();

    [Fact]
    public async Task Health_reports_calc_state()
    {
        var r = await Client(true).GetFromJsonAsync<HealthResponse>("/api/system/health");
        Assert.Equal(new HealthResponse("ok", "ok"), r);

        r = await Client(false).GetFromJsonAsync<HealthResponse>("/api/system/health");
        Assert.Equal(new HealthResponse("ok", "unavailable"), r);
    }

    [Fact]
    public async Task Rules_are_proxied_from_calc()
    {
        var resp = await Client(true).GetAsync("/api/system/rules");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<List<string>>();
        Assert.Equal(["eskom/0.1.0"], list);
    }
}
