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
        var issue = stored.Issues.Single(i => i.Code == "near_miss");
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

    private static async Task<Guid> AddHouseAsync(HttpClient client, Guid projectId, double dx, bool withLoad)
    {
        var id = Guid.NewGuid();
        var r = await client.PostAsJsonAsync($"/api/projects/{projectId}/buildings/new",
            new NewBuildingRequest(id, Guid.NewGuid(), "house", new PositionDto(Lon + dx, Lat + 0.0001, 3), DateTimeOffset.UtcNow, null));
        r.EnsureSuccessStatusCode();
        if (withLoad)
            (await client.PutAsJsonAsync($"/api/projects/{projectId}/buildings/{id}/load", new LoadRequest("residential", [], null, null, null, null)))
                .EnsureSuccessStatusCode();
        return id;
    }

    [Fact]
    public async Task Connects_the_loads_and_stores_their_boxes_and_phases()
    {
        factory.Calc.OnBuildLvNetwork = FakeCalc.DefaultLvNetwork;
        factory.Calc.OnAllocateLvLoads = FakeCalc.DefaultLvLoads;
        var (client, projectId) = await NewProjectAsync();
        await MarkAsync(client, projectId, "transformer", Point());
        await MarkAsync(client, projectId, "lv_route", Line());
        var house = await AddHouseAsync(client, projectId, 0.0005, withLoad: true);
        var bare = await AddHouseAsync(client, projectId, 0.001, withLoad: false);

        await client.PostAsync($"/api/projects/{projectId}/lv-network", null);
        // Every building goes to the calc service: with its load point and kVA, or without when it has no estimate.
        var sent = factory.Calc.LastLvLoads.ToDictionary(l => Guid.Parse(l.BuildingId));
        Assert.Equal((1.5, "residential"), (sent[house].Kva, sent[house].Kind));
        Assert.NotEqual(house.ToString(), sent[house].Id);
        Assert.Equal((null, bare.ToString()), (sent[bare].Kva, sent[bare].Id));
        Assert.Equal(Lon + 0.0005, sent[house].Coordinates[0], 9);

        var net = (await GetAsync(client, projectId))!;
        Assert.Null(net.Stale);
        var loads = Assert.IsType<LvLoadsDto>(net.Loads);
        var c = Assert.Single(loads.Connections);
        Assert.Equal((house, "P1-1", "R", "TX1-F1", 12.5, "B1"), (c.BuildingId, c.Box, c.Phase, c.Feeder, c.ServiceM, c.Branch));
        Assert.Equal([[Lon + 0.0005, Lat + 0.0001], [Lon, Lat]], c.Service);
        Assert.Equal(("P1-1", 1), (loads.Boxes[0].Id, loads.Boxes[0].Loads));
        Assert.Equal((1, 1), (loads.Feeders[0].Phases["R"].Customers, loads.Feeders[0].Phases["R"].Boxes));
        Assert.Equal((2, 1, 1, 1), (loads.Summary.Loads, loads.Summary.Allocated, loads.Summary.Unestimated, loads.Summary.Boxes));
        Assert.Equal("test service practice", loads.Clause);
        Assert.Contains(net.Issues, i => i.Code == "no_load");

        // The checks get each connected load with its Herman-Beta class, and their results are stored.
        var at = Assert.Single(factory.Calc.LastLvLoadsAt);
        Assert.Equal((c.LoadPointId.ToString(), "township_area", "B1", "R"), (at.LoadId, at.LoadClass, at.Branch, at.Phase));
        var analysis = Assert.IsType<CalcLvAnalysis>(net.Analysis);
        Assert.Equal((4.2, 61.0, 812.0, true), (analysis.Feeders[0].MaxDropPct, analysis.Feeders[0].MaxUtilisationPct, analysis.Feeders[0].MinFaultA, analysis.Feeders[0].Passes));
        Assert.Contains(analysis.Points, p => p.Id == c.LoadPointId.ToString() && p.Kind == "connection");
        Assert.Equal(4.2, analysis.WorstDrop!.Value);
        Assert.Contains(net.Issues, i => i.Code == "placeholders");

        // Estimating the bare building's load makes the network out of date.
        (await client.PutAsJsonAsync($"/api/projects/{projectId}/buildings/{bare}/load", new LoadRequest("residential", [], null, null, null, null)))
            .EnsureSuccessStatusCode();
        Assert.Contains("loads changed", (await GetAsync(client, projectId))!.Stale);
        await client.PostAsync($"/api/projects/{projectId}/lv-network", null);
        Assert.Equal(2, (await GetAsync(client, projectId))!.Loads!.Connections.Count);
    }

    [Fact]
    public async Task Rules_without_load_settings_still_build_the_network_and_say_why_loads_are_missing()
    {
        factory.Calc.OnBuildLvNetwork = FakeCalc.DefaultLvNetwork;
        factory.Calc.OnAllocateLvLoads = (rules, _, _) => throw new CalcRejectedException($"rules {rules} has no lv_loads section");
        try
        {
            var (client, projectId) = await NewProjectAsync();
            await MarkAsync(client, projectId, "lv_route", Line());
            var r = await client.PostAsync($"/api/projects/{projectId}/lv-network", null);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var net = (await GetAsync(client, projectId))!;
            Assert.Null(net.Loads);
            Assert.Single(net.Branches);
            Assert.Contains(net.Issues, i => i.Code == "loads_skipped" && i.Message.Contains("lv_loads"));
        }
        finally
        {
            factory.Calc.OnAllocateLvLoads = FakeCalc.DefaultLvLoads;
        }
    }

    [Fact]
    public async Task Rules_without_design_settings_connect_loads_but_say_the_checks_were_skipped()
    {
        factory.Calc.OnBuildLvNetwork = FakeCalc.DefaultLvNetwork;
        factory.Calc.OnAnalyseLv = (rules, _, _) => throw new CalcRejectedException($"rules {rules} has no lv_design section");
        try
        {
            var (client, projectId) = await NewProjectAsync();
            await MarkAsync(client, projectId, "lv_route", Line());
            await AddHouseAsync(client, projectId, 0.0005, withLoad: true);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/projects/{projectId}/lv-network", null)).StatusCode);
            var net = (await GetAsync(client, projectId))!;
            Assert.NotNull(net.Loads);
            Assert.Null(net.Analysis);
            Assert.Contains(net.Issues, i => i.Code == "checks_skipped" && i.Message.Contains("lv_design"));
        }
        finally
        {
            factory.Calc.OnAnalyseLv = FakeCalc.DefaultLvAnalysis;
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

    [Fact]
    public async Task Serves_the_conductor_library_of_the_projects_rules()
    {
        var (client, projectId) = await NewProjectAsync();
        var library = (await client.GetFromJsonAsync<CalcConductorLibrary>($"/api/projects/{projectId}/conductors"))!;
        Assert.Equal("eskom/0.1.0", library.RulesRef);
        var c = Assert.Single(library.Conductors);
        Assert.Equal(("CU-4C-70", 171.0, 8.05), (c.Code, c.RatingsA["pipe"], c.OneSecondKa));
        Assert.Equal(["r_ohm_per_km", "x_ohm_per_km"], c.Placeholder);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{Guid.NewGuid()}/conductors")).StatusCode);
    }
}
