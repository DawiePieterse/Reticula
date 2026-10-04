using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Reticula.Api.Auth;
using Reticula.Api.Design;
using Reticula.Api.Field;
using Reticula.Api.Jobs;
using Reticula.Api.Layout;
using Reticula.Api.Projects;
using Reticula.Domain.Auth;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Geo;

namespace Reticula.Api.Tests;

/// <summary>LV design runs (plan Phase 2): the job gathers the field data, calls the calc service and stores the result.</summary>
[Collection(ApiCollection.Name)]
public class DesignTests(ReticulaApiFactory factory)
{
    private const double Lon = 28.10, Lat = -25.52;

    private static PolygonDto Square(double lon, double lat, double d) =>
        new("Polygon", [[[lon, lat], [lon + d, lat], [lon + d, lat + d], [lon, lat + d], [lon, lat]]]);

    private static GeometryInput Point(double lon, double lat) => new("Point", JsonSerializer.SerializeToElement(new[] { lon, lat }));

    private sealed record Ctx(HttpClient Engineer, Guid ProjectId, List<Guid> Buildings, Guid Transformer);

    private async Task<Ctx> SetupAsync(bool loads = true, string rules = "eskom/0.3.0")
    {
        factory.Calc.Rules.Add(rules);
        var client = await factory.EngineerClientAsync();
        var p = await (await client.PostAsJsonAsync("/api/projects", new SaveProjectRequest($"Design {Guid.NewGuid():N}", rules, Square(28.09, -25.53, 0.03), null)))
            .Content.ReadFromJsonAsync<ProjectDto>();
        factory.Calc.OnImport = r => new CalcImportResult(r.Kind, "kml", "WGS84", "test",
            [new CalcFeature("s1", Square(Lon, Lat, 0.001), 5000, "7001", "Residential 1", null, [], [])], [], []);
        await UploadAsync(client, p!.Id, "stands");
        factory.Calc.OnImport = r => new CalcImportResult(r.Kind, "geojson", "WGS84", "test",
            [.. Enumerable.Range(0, 3).Select(i => new CalcFeature($"way/{i}", Square(Lon + 0.0002 * (i + 1), Lat + 0.0002, 0.0001), 80, null, null, $"way/{i}", new() { ["building"] = "yes" }, []))], [], []);
        await UploadAsync(client, p.Id, "buildings");
        var fc = await client.GetFromJsonAsync<GeoFeatureCollection<BuildingProps>>($"/api/projects/{p.Id}/buildings");
        var buildings = fc!.Features.Select(f => Guid.Parse(f.Id)).ToList();

        var tx = Guid.NewGuid();
        (await client.PutAsJsonAsync($"/api/projects/{p.Id}/candidates/{tx}", new CandidateRequest("transformer", Point(Lon, Lat + 0.0001), null, null, null, null))).EnsureSuccessStatusCode();
        var line = new GeometryInput("LineString", JsonSerializer.SerializeToElement(new[] { new[] { Lon, Lat + 0.0001 }, new[] { Lon + 0.001, Lat + 0.0001 } }));
        (await client.PutAsJsonAsync($"/api/projects/{p.Id}/candidates/{Guid.NewGuid()}", new CandidateRequest("lv_route", line, null, null, null, null))).EnsureSuccessStatusCode();

        if (loads)
        {
            (await client.PutAsJsonAsync($"/api/projects/{p.Id}/buildings/{buildings[0]}/load", new LoadRequest("residential", [], null, null, null, null, null, 3))).EnsureSuccessStatusCode();
            (await client.PutAsJsonAsync($"/api/projects/{p.Id}/buildings/{buildings[1]}/load", new LoadRequest("residential", [], null, null, null, null))).EnsureSuccessStatusCode();
            (await client.PutAsJsonAsync($"/api/projects/{p.Id}/buildings/{buildings[2]}/load", new LoadRequest("special", null, "school", null, null, null))).EnsureSuccessStatusCode();
        }
        return new Ctx(client, p.Id, buildings, tx);
    }

    private static async Task UploadAsync(HttpClient client, Guid projectId, string kind)
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent("x"u8.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "file", "f.kml" },
            { new StringContent(kind), "kind" },
        };
        (await client.PostAsync($"/api/projects/{projectId}/imports", form)).EnsureSuccessStatusCode();
    }

    private static async Task<DesignRunDetail> WaitAsync(HttpClient client, Guid projectId, Guid runId)
    {
        for (var i = 0; i < 200; i++)
        {
            var d = await client.GetFromJsonAsync<DesignRunDetail>($"/api/projects/{projectId}/lv-designs/{runId}");
            if (d!.Run.Status is "succeeded" or "failed") return d;
            await Task.Delay(100);
        }
        throw new TimeoutException();
    }

    [Fact]
    public async Task Design_run_sends_the_field_data_and_stores_the_result()
    {
        var ctx = await SetupAsync();
        var r = await ctx.Engineer.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/lv-designs",
            new LvDesignRequest(ctx.Transformer, ["overhead", "underground"], null, null));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var started = await r.Content.ReadFromJsonAsync<StartedDesign>();
        Assert.Equal("design.lv", started!.Job.Kind);

        var done = await WaitAsync(ctx.Engineer, ctx.ProjectId, started.Run.Id);
        Assert.Equal("succeeded", done.Run.Status);
        Assert.True(done.Run.Passed);
        Assert.Equal("abcdef0123456789", done.Run.RulesHash);
        Assert.Equal("overhead", done.Result!.Value.GetProperty("comparison")[0].GetProperty("construction").GetString());

        var sent = factory.Calc.LastLvDesign!.Value;
        Assert.Equal("eskom/0.3.0", sent.GetProperty("rules").GetString());
        Assert.Equal(Lon, sent.GetProperty("source")[0].GetDouble(), 9);
        Assert.Equal(["overhead", "underground"], sent.GetProperty("constructions").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(1, sent.GetProperty("routes").GetArrayLength());
        var customers = sent.GetProperty("customers").EnumerateArray().ToList();
        Assert.Equal(3, customers.Count);
        var three = customers.Single(c => c.GetProperty("building_id").GetString() == ctx.Buildings[0].ToString());
        Assert.Equal(3, three.GetProperty("phases").GetInt32());
        Assert.Equal("township_area", three.GetProperty("load_class").GetString());
        Assert.False(three.GetProperty("inspected").GetBoolean());
        var school = customers.Single(c => c.GetProperty("kind").GetString() == "special");
        Assert.Equal(25, school.GetProperty("special_kva").GetDouble());

        var list = await ctx.Engineer.GetFromJsonAsync<List<DesignRunDto>>($"/api/projects/{ctx.ProjectId}/lv-designs");
        Assert.Equal(started.Run.Id, Assert.Single(list!).Id);
    }

    [Fact]
    public async Task Buildings_without_loads_fail_the_run_with_the_reason()
    {
        var ctx = await SetupAsync(loads: false);
        var started = await (await ctx.Engineer.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/lv-designs", new LvDesignRequest(ctx.Transformer, null, null, null)))
            .Content.ReadFromJsonAsync<StartedDesign>();
        var done = await WaitAsync(ctx.Engineer, ctx.ProjectId, started!.Run.Id);
        Assert.Equal("failed", done.Run.Status);
        Assert.Contains("3 buildings have no load recorded", done.Run.Error);
    }

    [Fact]
    public async Task Design_requests_are_validated_and_engineer_only()
    {
        var ctx = await SetupAsync(loads: false);
        var bad = await ctx.Engineer.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/lv-designs", new LvDesignRequest(Guid.NewGuid(), ["aerial"], -5, null));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var problem = await bad.Content.ReadFromJsonAsync<JsonElement>();
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("constructions", out _) && errors.TryGetProperty("transformerCandidateId", out _) && errors.TryGetProperty("transformerKva", out _));

        var email = $"inspector-{Guid.NewGuid():N}@test.local";
        (await ctx.Engineer.PostAsJsonAsync("/api/users", new CreateUserRequest(email, "Inspector", "inspector-pass-1", Roles.Inspector, null))).EnsureSuccessStatusCode();
        var inspector = await factory.ClientAsAsync(email, "inspector-pass-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/lv-designs", new LvDesignRequest(ctx.Transformer, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await inspector.GetAsync($"/api/projects/{ctx.ProjectId}/lv-designs")).StatusCode);
    }

    [Fact]
    public async Task Connection_phases_are_stored_with_the_load_and_reach_group_demand()
    {
        var ctx = await SetupAsync();
        var points = await ctx.Engineer.GetFromJsonAsync<List<LoadPointDto>>($"/api/projects/{ctx.ProjectId}/load-points");
        Assert.Equal(3, points!.Single(l => l.BuildingId == ctx.Buildings[0]).Phases);
        Assert.Equal(1, points!.Single(l => l.BuildingId == ctx.Buildings[1]).Phases);
        await ctx.Engineer.GetAsync($"/api/projects/{ctx.ProjectId}/load-schedule");
        Assert.Contains(factory.Calc.LastGroupLoads, l => l.Phases == 3);
        var bad = await ctx.Engineer.PutAsJsonAsync($"/api/projects/{ctx.ProjectId}/buildings/{ctx.Buildings[1]}/load", new LoadRequest("residential", [], null, null, null, null, null, 2));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    private static async Task<DesignRunDetail> WaitMvAsync(HttpClient client, Guid projectId, Guid runId)
    {
        for (var i = 0; i < 200; i++)
        {
            var d = await client.GetFromJsonAsync<DesignRunDetail>($"/api/projects/{projectId}/mv-designs/{runId}");
            if (d!.Run.Status is "succeeded" or "failed") return d;
            await Task.Delay(100);
        }
        throw new TimeoutException();
    }

    private static async Task AddMvRouteAsync(HttpClient client, Guid projectId)
    {
        var line = new GeometryInput("LineString", JsonSerializer.SerializeToElement(new[] { new[] { Lon - 0.005, Lat + 0.002 }, new[] { Lon, Lat + 0.0001 } }));
        (await client.PutAsJsonAsync($"/api/projects/{projectId}/candidates/{Guid.NewGuid()}", new CandidateRequest("mv_route", line, null, null, null, null))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Mv_design_uses_the_authority_connection_point_when_one_is_imported()
    {
        var ctx = await SetupAsync(rules: "eskom/0.4.0");
        await AddMvRouteAsync(ctx.Engineer, ctx.ProjectId);
        var attrs = new Dictionary<string, JsonElement> { ["asset_type"] = JsonSerializer.SerializeToElement("connection_point") };
        factory.Calc.OnImport = r => new CalcImportResult(r.Kind, "geojson", "WGS84", "test",
            [new CalcFeature("CP-1", Point(Lon - 0.005, Lat + 0.002), 0, null, null, null, [], attrs, 0, "Mabopane CP", "connection_point")], [], []);
        await UploadAsync(ctx.Engineer, ctx.ProjectId, "network");

        var r = await ctx.Engineer.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/mv-designs", new MvDesignRequest(null, "overhead", "overhead", null));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var started = await r.Content.ReadFromJsonAsync<StartedDesign>();
        Assert.Equal("design.mv", started!.Job.Kind);
        var done = await WaitMvAsync(ctx.Engineer, ctx.ProjectId, started.Run.Id);
        Assert.Equal("succeeded", done.Run.Status);
        Assert.True(done.Run.Passed);

        var sent = factory.Calc.LastMvDesign!.Value;
        Assert.Equal("eskom/0.4.0", sent.GetProperty("rules").GetString());
        Assert.Equal(Lon - 0.005, sent.GetProperty("supply")[0].GetDouble(), 9);
        Assert.Contains("Mabopane CP", sent.GetProperty("supply_note").GetString());
        Assert.Equal(1, sent.GetProperty("sites").GetArrayLength());
        Assert.Equal("transformer", sent.GetProperty("sites")[0].GetProperty("kind").GetString());
        Assert.Equal(1, sent.GetProperty("mv_routes").GetArrayLength());
        Assert.Equal(3, sent.GetProperty("customers").GetArrayLength());
        Assert.Single((await ctx.Engineer.GetFromJsonAsync<List<DesignRunDto>>($"/api/projects/{ctx.ProjectId}/mv-designs"))!);
        Assert.Empty((await ctx.Engineer.GetFromJsonAsync<List<DesignRunDto>>($"/api/projects/{ctx.ProjectId}/lv-designs"))!);
    }

    [Fact]
    public async Task Mv_design_without_an_mv_route_fails_and_a_given_supply_point_is_used_as_is()
    {
        var ctx = await SetupAsync(rules: "eskom/0.4.0");
        var started = await (await ctx.Engineer.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/mv-designs", new MvDesignRequest(null, null, null, null)))
            .Content.ReadFromJsonAsync<StartedDesign>();
        var failed = await WaitMvAsync(ctx.Engineer, ctx.ProjectId, started!.Run.Id);
        Assert.Equal("failed", failed.Run.Status);
        Assert.Contains("MV route", failed.Run.Error);

        await AddMvRouteAsync(ctx.Engineer, ctx.ProjectId);
        started = await (await ctx.Engineer.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/mv-designs", new MvDesignRequest([ctx.Transformer], "underground", "underground", [28.08, -25.51])))
            .Content.ReadFromJsonAsync<StartedDesign>();
        Assert.Equal("succeeded", (await WaitMvAsync(ctx.Engineer, ctx.ProjectId, started!.Run.Id)).Run.Status);
        var sent = factory.Calc.LastMvDesign!.Value;
        Assert.Equal(28.08, sent.GetProperty("supply")[0].GetDouble());
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("supply_note").ValueKind);
        Assert.Equal("underground", sent.GetProperty("lv_construction").GetString());

        var bad = await ctx.Engineer.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/mv-designs", new MvDesignRequest([Guid.NewGuid()], "aerial", null, [1]));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
