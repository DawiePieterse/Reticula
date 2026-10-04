using System.Net;
using System.Net.Http.Json;
using Reticula.Api.Auth;
using Reticula.Api.Geo;
using Reticula.Api.Projects;
using Reticula.Domain.Auth;

namespace Reticula.Api.Tests;

public class ProjectTests(ReticulaApiFactory factory) : IClassFixture<ReticulaApiFactory>
{
    // A small square in Soshanguve, Gauteng.
    private static PolygonDto Square(double lon = 28.10, double lat = -25.52, double d = 0.01) =>
        new("Polygon", [[[lon, lat], [lon + d, lat], [lon + d, lat + d], [lon, lat + d], [lon, lat]]]);

    private static SaveProjectRequest NewRequest(string name = "Soshanguve Ext 19") =>
        new(name, "eskom/0.1.0", Square(), null);

    [Fact]
    public async Task Engineer_creates_and_reads_project()
    {
        var client = await factory.EngineerClientAsync();
        var r = await client.PostAsJsonAsync("/api/projects", NewRequest());
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var created = await r.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.Equal("eskom", created!.Authority);
        Assert.NotEqual(0u, created.Version);

        var fetched = await client.GetFromJsonAsync<ProjectDto>($"/api/projects/{created.Id}");
        Assert.Equal(Square().Coordinates, fetched!.Area.Coordinates);

        var list = await client.GetFromJsonAsync<List<ProjectDto>>("/api/projects");
        Assert.Contains(list!, p => p.Id == created.Id);
    }

    [Fact]
    public async Task Self_intersecting_area_is_rejected()
    {
        var client = await factory.EngineerClientAsync();
        var bowTie = new PolygonDto("Polygon", [[[28.10, -25.52], [28.11, -25.51], [28.11, -25.52], [28.10, -25.51], [28.10, -25.52]]]);
        var r = await client.PostAsJsonAsync("/api/projects", NewRequest() with { Area = bowTie });
        await AssertValidationError(r, "area");
    }

    [Fact]
    public async Task Area_outside_South_Africa_is_rejected()
    {
        var client = await factory.EngineerClientAsync();
        var r = await client.PostAsJsonAsync("/api/projects", NewRequest() with { Area = Square(lon: 2.35, lat: 48.85) });
        await AssertValidationError(r, "area");
    }

    [Fact]
    public async Task Unclosed_ring_is_rejected()
    {
        var client = await factory.EngineerClientAsync();
        var open = new PolygonDto("Polygon", [[[28.10, -25.52], [28.11, -25.52], [28.11, -25.51], [28.10, -25.51]]]);
        var r = await client.PostAsJsonAsync("/api/projects", NewRequest() with { Area = open });
        await AssertValidationError(r, "area");
    }

    [Fact]
    public async Task Unknown_or_malformed_rules_are_rejected()
    {
        var client = await factory.EngineerClientAsync();
        await AssertValidationError(await client.PostAsJsonAsync("/api/projects", NewRequest() with { RulesRef = "tshwane/9.9.9" }), "rulesRef");
        await AssertValidationError(await client.PostAsJsonAsync("/api/projects", NewRequest() with { RulesRef = "../etc" }), "rulesRef");
    }

    [Fact]
    public async Task Calc_unavailable_returns_503()
    {
        var client = await factory.EngineerClientAsync();
        factory.Calc.Unreachable = true;
        try
        {
            var r = await client.PostAsJsonAsync("/api/projects", NewRequest());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        }
        finally
        {
            factory.Calc.Unreachable = false;
        }
    }

    [Fact]
    public async Task Stale_update_returns_409_and_does_not_overwrite()
    {
        var client = await factory.EngineerClientAsync();
        var created = await (await client.PostAsJsonAsync("/api/projects", NewRequest("Original"))).Content.ReadFromJsonAsync<ProjectDto>();

        var first = await client.PutAsJsonAsync($"/api/projects/{created!.Id}", NewRequest("First edit") with { Version = created.Version });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var updated = await first.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotEqual(created.Version, updated!.Version);

        var stale = await client.PutAsJsonAsync($"/api/projects/{created.Id}", NewRequest("Stale edit") with { Version = created.Version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var current = await client.GetFromJsonAsync<ProjectDto>($"/api/projects/{created.Id}");
        Assert.Equal("First edit", current!.Name);
    }

    [Fact]
    public async Task Update_requires_version()
    {
        var client = await factory.EngineerClientAsync();
        var created = await (await client.PostAsJsonAsync("/api/projects", NewRequest())).Content.ReadFromJsonAsync<ProjectDto>();
        var r = await client.PutAsJsonAsync($"/api/projects/{created!.Id}", NewRequest());
        await AssertValidationError(r, "version");
    }

    [Fact]
    public async Task Archived_project_disappears()
    {
        var client = await factory.EngineerClientAsync();
        var created = await (await client.PostAsJsonAsync("/api/projects", NewRequest())).Content.ReadFromJsonAsync<ProjectDto>();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/projects/{created!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{created.Id}")).StatusCode);
        var list = await client.GetFromJsonAsync<List<ProjectDto>>("/api/projects");
        Assert.DoesNotContain(list!, p => p.Id == created.Id);
    }

    [Fact]
    public async Task Inspector_can_read_but_not_write()
    {
        var engineer = await factory.EngineerClientAsync();
        var created = await (await engineer.PostAsJsonAsync("/api/projects", NewRequest())).Content.ReadFromJsonAsync<ProjectDto>();

        var email = $"inspector-{Guid.NewGuid():N}@test.local";
        (await engineer.PostAsJsonAsync("/api/users", new CreateUserRequest(email, "Inspector", "inspector-pass-1", Roles.Inspector, null))).EnsureSuccessStatusCode();
        var inspector = await factory.ClientAsAsync(email, "inspector-pass-1");

        Assert.Equal(HttpStatusCode.OK, (await inspector.GetAsync($"/api/projects/{created!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.PostAsJsonAsync("/api/projects", NewRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.PutAsJsonAsync($"/api/projects/{created.Id}", NewRequest() with { Version = created.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.DeleteAsync($"/api/projects/{created.Id}")).StatusCode);
    }

    private static async Task AssertValidationError(HttpResponseMessage r, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<ValidationBody>();
        Assert.True(body!.Errors.ContainsKey(field), $"expected error for '{field}', got: {string.Join(", ", body.Errors.Keys)}");
    }

    private sealed record ValidationBody(Dictionary<string, string[]> Errors);
}
