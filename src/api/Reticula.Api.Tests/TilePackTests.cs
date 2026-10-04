using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reticula.Api.Jobs;
using Reticula.Api.Maps;
using Reticula.Api.Projects;
using Reticula.Infrastructure.Geo;
using Reticula.Infrastructure.Maps;

namespace Reticula.Api.Tests;

[Collection(ApiCollection.Name)]
public class TilePackTests(ReticulaApiFactory factory)
{
    private static PolygonDto Square(double lon, double lat, double d) =>
        new("Polygon", [[[lon, lat], [lon + d, lat], [lon + d, lat + d], [lon, lat + d], [lon, lat]]]);

    private async Task<(HttpClient Client, Guid ProjectId)> ProjectAsync()
    {
        var client = await factory.EngineerClientAsync();
        var p = await (await client.PostAsJsonAsync("/api/projects", new SaveProjectRequest($"Tiles {Guid.NewGuid():N}", "eskom/0.1.0", Square(28.09, -25.53, 0.01), null)))
            .Content.ReadFromJsonAsync<ProjectDto>();
        return (client, p!.Id);
    }

    private static async Task<JobDto> WaitAsync(HttpClient client, Guid jobId)
    {
        for (var i = 0; i < 200; i++)
        {
            var job = await client.GetFromJsonAsync<JobDto>($"/api/jobs/{jobId}");
            if (job!.Status is "succeeded" or "failed" or "cancelled") return job;
            await Task.Delay(100);
        }
        throw new TimeoutException();
    }

    [Fact]
    public async Task Builds_a_pack_for_the_project_area_and_serves_it()
    {
        var (client, projectId) = await ProjectAsync();
        var before = await client.GetFromJsonAsync<TilePackStatus>($"/api/projects/{projectId}/tile-pack");
        Assert.True(before!.SourceConfigured);
        Assert.Null(before.Pack);
        Assert.Equal((12, 16), (before.Estimate!.MinZoom, before.Estimate.MaxZoom));

        factory.Tiles.Missing = u => u.AbsolutePath.StartsWith("/16/") && int.Parse(u.AbsolutePath.Split('/')[2]) % 2 == 0; // the server has gaps
        try
        {
            var r = await client.PostAsync($"/api/projects/{projectId}/tile-pack", null);
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
            var job = await WaitAsync(client, (await r.Content.ReadFromJsonAsync<JobDto>())!.Id);
            Assert.Equal("succeeded", job.Status);
            Assert.True(job.Result!.Value.GetProperty("missing").GetInt32() > 0, job.Result.Value.ToString());
        }
        finally
        {
            factory.Tiles.Missing = _ => false;
        }

        var status = await client.GetFromJsonAsync<TilePackStatus>($"/api/projects/{projectId}/tile-pack");
        var pack = status!.Pack!;
        Assert.Equal("http://tiles.test/{z}/{x}/{y}.png", pack.Source); // key stripped
        Assert.True(pack.TileCount > 0 && pack.TileCount < before.Estimate.Tiles);

        var file = await client.GetAsync($"/api/projects/{projectId}/tile-pack/file");
        Assert.Equal("application/vnd.pmtiles", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"\"{pack.Sha256}\"", file.Headers.ETag!.Tag);
        var bytes = await file.Content.ReadAsByteArrayAsync();
        Assert.Equal(pack.SizeBytes, bytes.Length);
        var reader = new PmTilesReader(bytes);
        Assert.Equal(((ulong)pack.TileCount, 2, 12, 16), (reader.AddressedTiles, reader.TileType, reader.MinZoom, reader.MaxZoom));
        var (x, y) = (TileMath.LonToX(28.095, 15), TileMath.LatToY(-25.525, 15));
        Assert.EndsWith($"/15/{x}/{y}.png", System.Text.Encoding.ASCII.GetString(reader.Tile(15, x, y)!));

        // Range requests work, so a client could also read the pack straight from the server.
        var range = new HttpRequestMessage(HttpMethod.Get, $"/api/projects/{projectId}/tile-pack/file");
        range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 126);
        var head = await client.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, head.StatusCode);
        Assert.Equal(127, (await head.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Rebuilding_replaces_the_pack_and_a_failing_tile_server_fails_the_job()
    {
        var (client, projectId) = await ProjectAsync();
        var first = await WaitAsync(client, (await (await client.PostAsync($"/api/projects/{projectId}/tile-pack", null)).Content.ReadFromJsonAsync<JobDto>())!.Id);
        Assert.Equal("succeeded", first.Status);

        factory.Tiles.Down = true;
        try
        {
            var job = await WaitAsync(client, (await (await client.PostAsync($"/api/projects/{projectId}/tile-pack", null)).Content.ReadFromJsonAsync<JobDto>())!.Id);
            Assert.Equal("failed", job.Status);
            Assert.Contains("could not be downloaded", job.Error);
        }
        finally
        {
            factory.Tiles.Down = false;
        }
        // The earlier pack is still there.
        Assert.NotNull((await client.GetFromJsonAsync<TilePackStatus>($"/api/projects/{projectId}/tile-pack"))!.Pack);
    }

    [Fact]
    public async Task Without_a_tile_source_the_build_is_refused()
    {
        var (client, projectId) = await ProjectAsync();
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var source = config["Tiles:SourceUrl"];
        config["Tiles:SourceUrl"] = "";
        try
        {
            var status = await client.GetFromJsonAsync<TilePackStatus>($"/api/projects/{projectId}/tile-pack");
            Assert.False(status!.SourceConfigured);
            var r = await client.PostAsync($"/api/projects/{projectId}/tile-pack", null);
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{projectId}/tile-pack/file")).StatusCode);
        }
        finally
        {
            config["Tiles:SourceUrl"] = source;
        }
    }
}
