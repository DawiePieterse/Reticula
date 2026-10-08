using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Reticula.Api.Jobs;
using Reticula.Api.Maps;
using Reticula.Api.Projects;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Geo;
using static Reticula.Api.Tests.TestFixtures;

namespace Reticula.Api.Tests;

[Collection(ApiCollection.Name)]
public class MapPackTests(ReticulaApiFactory factory)
{
    private Task<(HttpClient Client, Guid ProjectId)> ProjectAsync() => factory.NewProjectAsync("Map", Square(28.10, -25.52, 0.02));

    private static async Task<JobDto> BuildAsync(HttpClient client, Guid projectId)
    {
        var r = await client.PostAsync($"/api/projects/{projectId}/map-pack", null);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var job = (await r.Content.ReadFromJsonAsync<JobDto>())!;
        return await WaitFinishedAsync(client, job.Id);
    }

    [Fact]
    public async Task Builds_a_pack_for_the_project_area_with_a_margin_and_serves_it_in_ranges()
    {
        var (client, projectId) = await ProjectAsync();
        var status = await client.GetFromJsonAsync<MapPackStatus>($"/api/projects/{projectId}/map-pack");
        Assert.Equal((null, null), (status!.Pack, status.Job));

        var job = await BuildAsync(client, projectId);
        Assert.Equal("succeeded", job.Status);
        Assert.Equal([28.095, -25.525, 28.125, -25.495], factory.Calc.LastExtractBox!.Select(v => Math.Round(v, 6)));

        status = await client.GetFromJsonAsync<MapPackStatus>($"/api/projects/{projectId}/map-pack");
        var pack = status!.Pack!;
        Assert.Null(status.Job);
        Assert.Equal((128L, 42, 15, "test-planet.pmtiles"), (pack.SizeBytes, pack.TileCount, pack.MaxZoom, pack.Source));
        Assert.Equal(64, pack.Sha256.Length);

        var url = $"/api/projects/{projectId}/map-pack/{pack.Id}.pmtiles";
        var whole = await client.GetAsync(url);
        Assert.Equal("application/vnd.pmtiles", whole.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"\"{pack.Sha256}\"", whole.Headers.ETag!.Tag);
        Assert.Equal("PMTiles"u8.ToArray(), (await whole.Content.ReadAsByteArrayAsync())[..7]);

        var range = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(0, 6) } };
        var part = await client.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal("PMTiles"u8.ToArray(), await part.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Rebuilding_replaces_the_pack_and_retires_the_old_file()
    {
        var (client, projectId) = await ProjectAsync();
        await BuildAsync(client, projectId);
        var first = (await client.GetFromJsonAsync<MapPackStatus>($"/api/projects/{projectId}/map-pack"))!.Pack!;

        factory.Calc.OnExtract = _ => new MapExtract([.. "PMTiles"u8, 3, .. new byte[200]], 50, 15, "newer.pmtiles");
        try
        {
            Assert.Equal("succeeded", (await BuildAsync(client, projectId)).Status);
        }
        finally
        {
            factory.Calc.OnExtract = _ => new MapExtract([.. "PMTiles"u8, 3, .. new byte[120]], 42, 15, "test-planet.pmtiles");
        }
        var second = (await client.GetFromJsonAsync<MapPackStatus>($"/api/projects/{projectId}/map-pack"))!.Pack!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("newer.pmtiles", second.Source);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{projectId}/map-pack/{first.Id}.pmtiles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/projects/{projectId}/map-pack/{second.Id}.pmtiles")).StatusCode);
        Assert.Empty(Directory.GetFiles(factory.StorageRoot, $"{first.Id:N}.pmtiles", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_refused_build_reports_the_reason_and_keeps_no_pack()
    {
        var (client, projectId) = await ProjectAsync();
        factory.Calc.OnExtract = _ => throw new CalcRejectedException("No offline map source is configured. Set RETICULA_MAP_SOURCE.");
        try
        {
            var job = await BuildAsync(client, projectId);
            Assert.Equal("failed", job.Status);
            var status = (await client.GetFromJsonAsync<MapPackStatus>($"/api/projects/{projectId}/map-pack"))!;
            Assert.Null(status.Pack);
            Assert.Contains("RETICULA_MAP_SOURCE", status.Job!.Error);
        }
        finally
        {
            factory.Calc.OnExtract = _ => new MapExtract([.. "PMTiles"u8, 3, .. new byte[120]], 42, 15, "test-planet.pmtiles");
        }
    }

    [Fact]
    public async Task Inspectors_can_build_and_download_but_unknown_projects_are_not_found()
    {
        var (client, projectId) = await ProjectAsync();
        var email = $"inspector-{Guid.NewGuid():N}@test.local";
        (await client.PostAsJsonAsync("/api/users", new Auth.CreateUserRequest(email, "Inspector", "inspector-pass-1", Domain.Auth.Roles.Inspector, null))).EnsureSuccessStatusCode();
        var inspector = await factory.ClientAsAsync(email, "inspector-pass-1");
        Assert.Equal("succeeded", (await BuildAsync(inspector, projectId)).Status);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{Guid.NewGuid()}/map-pack")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/projects/{Guid.NewGuid()}/map-pack", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{projectId}/map-pack/{Guid.NewGuid()}.pmtiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/projects/{projectId}/map-pack")).StatusCode);
    }
}
