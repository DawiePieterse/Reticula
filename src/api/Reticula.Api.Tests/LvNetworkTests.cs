using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Reticula.Api.Auth;
using Reticula.Api.Design;
using Reticula.Api.Field;
using Reticula.Api.Projects;
using Reticula.Domain.Auth;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Geo;

namespace Reticula.Api.Tests;

/// <summary>The LV network model (plan 2.1): built by the calc service from the marked routes and sites, stored per project.</summary>
[Collection(ApiCollection.Name)]
public class LvNetworkTests(ReticulaApiFactory factory)
{
    private const double Lon = 28.10, Lat = -25.52;

    private static PolygonDto Square(double lon, double lat, double d) =>
        new("Polygon", [[[lon, lat], [lon + d, lat], [lon + d, lat + d], [lon, lat + d], [lon, lat]]]);

    private static GeometryInput Point(double dx = 0) => new("Point", JsonSerializer.SerializeToElement(new[] { Lon + dx, Lat }));

    private static GeometryInput Line(double dy = 0) =>
        new("LineString", JsonSerializer.SerializeToElement(new[] { new[] { Lon, Lat + dy }, new[] { Lon + 0.002, Lat + dy } }));

    private async Task<(HttpClient Client, Guid ProjectId)> NewProjectAsync()
    {
        var client = await factory.EngineerClientAsync();
        var r = await client.PostAsJsonAsync("/api/projects", new SaveProjectRequest($"LV {Guid.NewGuid():N}", "eskom/0.1.0", Square(28.09, -25.53, 0.03), null));
        return (client, (await r.Content.ReadFromJsonAsync<ProjectDto>())!.Id);
    }

    private static async Task<Guid> MarkAsync(HttpClient client, Guid projectId, string kind, GeometryInput geometry)
    {
        var id = Guid.NewGuid();
        var r = await client.PutAsJsonAsync($"/api/projects/{projectId}/candidates/{id}", new CandidateRequest(kind, geometry, null, null, null, null));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return id;
    }

    private static async Task<LvNetworkDto?> GetAsync(HttpClient client, Guid projectId) =>
        (await client.GetFromJsonAsync<LvNetworkStatus>($"/api/projects/{projectId}/lv-network"))!.Network;

    [Fact]
    public async Task Builds_from_the_marked_lv_routes_and_sites_and_stores_the_model()
    {
        factory.Calc.OnBuildLvNetwork = FakeCalc.DefaultLvNetwork;
        var (client, projectId) = await NewProjectAsync();
        Assert.Null(await GetAsync(client, projectId));

        var tx = await MarkAsync(client, projectId, "transformer", Point());
        var route = await MarkAsync(client, projectId, "lv_route", Line());
        await MarkAsync(client, projectId, "mv_route", Line(0.001));

        var r = await client.PostAsync($"/api/projects/{projectId}/lv-network", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var built = (await r.Content.ReadFromJsonAsync<LvNetworkDto>())!;

        // MV routes are for Phase 3 and are not sent.
        Assert.Equal([("transformer", tx.ToString()), ("lv_route", route.ToString())], factory.Calc.LastLvCandidates.Select(c => (c.Kind, c.Id)));
        Assert.Equal("LineString", factory.Calc.LastLvCandidates[1].Geometry.GetProperty("type").GetString());

        var stored = (await GetAsync(client, projectId))!;
        Assert.Equal(built.Id, stored.Id);
        Assert.Null(stored.Stale);
        Assert.Equal(("eskom/0.1.0", "0123456789abcdef", "test tolerances"), (stored.RulesRef, stored.RulesHash, stored.Clause));
        Assert.Equal(["N1", "N2", "N3"], stored.Nodes.Select(n => n.Id));
        var source = stored.Nodes[0];
        Assert.Equal(("source", "TX1", tx, 0.0), (source.Kind, source.Label, source.CandidateId, source.DistanceM));
        Assert.Equal([Lon, Lat], source.Coordinates);
        var branch = Assert.Single(stored.Branches);
        Assert.Equal(("B1", "route", "N2", "N3", route, 100.0), (branch.Id, branch.Kind, branch.FromNode, branch.ToNode, branch.CandidateId, branch.LengthM));
        Assert.Equal([[Lon, Lat], [Lon + 0.002, Lat]], branch.Coordinates);
        Assert.Equal("TX1-F1", Assert.Single(stored.Feeders).Id);
        var issue = Assert.Single(stored.Issues);
        Assert.Equal(("near_miss", 28.1), (issue.Code, issue.At[0][0]));
        Assert.Equal((1, 1, 100.0), (stored.Summary.Routes, stored.Summary.Feeders, stored.Summary.RouteLengthM));
    }

    [Fact]
    public async Task Says_when_the_network_is_out_of_date_and_a_rebuild_replaces_it()
    {
        factory.Calc.OnBuildLvNetwork = FakeCalc.DefaultLvNetwork;
        var (client, projectId) = await NewProjectAsync();
        await MarkAsync(client, projectId, "transformer", Point());
        await client.PostAsync($"/api/projects/{projectId}/lv-network", null);
        Assert.Null((await GetAsync(client, projectId))!.Stale);

        await MarkAsync(client, projectId, "lv_route", Line());
        var stale = (await GetAsync(client, projectId))!;
        Assert.Contains("after the network was built", stale.Stale);
        Assert.Empty(stale.Branches);

        await client.PostAsync($"/api/projects/{projectId}/lv-network", null);
        var rebuilt = (await GetAsync(client, projectId))!;
        Assert.NotEqual(stale.Id, rebuilt.Id);
        Assert.Null(rebuilt.Stale);
        Assert.Single(rebuilt.Branches);

        // Moving to other rules makes it stale too.
        factory.Calc.Rules.Add("eskom/0.3.0");
        try
        {
            var project = (await client.GetFromJsonAsync<ProjectDto>($"/api/projects/{projectId}"))!;
            var saved = await client.PutAsJsonAsync($"/api/projects/{projectId}", new SaveProjectRequest(project.Name, "eskom/0.3.0", project.Area, project.Version));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Contains("eskom/0.3.0", (await GetAsync(client, projectId))!.Stale);
        }
        finally
        {
            factory.Calc.Rules.Remove("eskom/0.3.0");
        }
    }

    [Fact]
    public async Task Rules_without_lv_tolerances_are_refused_and_only_engineers_build()
    {
        var (client, projectId) = await NewProjectAsync();
        factory.Calc.OnBuildLvNetwork = (rules, _) => throw new CalcRejectedException($"rules {rules} has no lv_network section");
        try
        {
            var r = await client.PostAsync($"/api/projects/{projectId}/lv-network", null);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("lv_network", await r.Content.ReadAsStringAsync());
            Assert.Null(await GetAsync(client, projectId));
        }
        finally
        {
            factory.Calc.OnBuildLvNetwork = FakeCalc.DefaultLvNetwork;
        }

        var email = $"inspector-{Guid.NewGuid():N}@test.local";
        (await client.PostAsJsonAsync("/api/users", new CreateUserRequest(email, "Inspector", "inspector-pass-1", Roles.Inspector, null))).EnsureSuccessStatusCode();
        var inspector = await factory.ClientAsAsync(email, "inspector-pass-1");
        Assert.Equal(HttpStatusCode.OK, (await inspector.GetAsync($"/api/projects/{projectId}/lv-network")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.PostAsync($"/api/projects/{projectId}/lv-network", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{Guid.NewGuid()}/lv-network")).StatusCode);
    }
}
