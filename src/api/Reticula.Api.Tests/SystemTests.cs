using System.Net;
using System.Net.Http.Json;

namespace Reticula.Api.Tests;

[Collection(ApiCollection.Name)]
public class SystemTests(ReticulaApiFactory factory)
{
    [Fact]
    public async Task Health_is_anonymous_and_reports_calc_state()
    {
        var client = factory.CreateClient();
        Assert.Equal(new HealthResponse("ok", "ok"), await client.GetFromJsonAsync<HealthResponse>("/api/system/health"));

        factory.Calc.Healthy = false;
        try
        {
            Assert.Equal(new HealthResponse("ok", "unavailable"), await client.GetFromJsonAsync<HealthResponse>("/api/system/health"));
        }
        finally
        {
            factory.Calc.Healthy = true;
        }
    }

    [Fact]
    public async Task Rules_list_requires_login_and_is_proxied()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/system/rules")).StatusCode);
        var client = await factory.EngineerClientAsync();
        Assert.Equal(["eskom/0.1.0"], await client.GetFromJsonAsync<List<string>>("/api/system/rules"));
    }
}
