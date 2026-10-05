using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Reticula.Api.Field;
using Reticula.Api.Jobs;
using Reticula.Api.Layout;
using Reticula.Api.Projects;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Geo;

namespace Reticula.Api.Tests;

/// <summary>Rooftop imagery and the rooftop classifier (plan 1.3).</summary>
[Collection(ApiCollection.Name)]
public class ImageryTests(ReticulaApiFactory factory)
{
    private const double Lon = 28.10, Lat = -25.52;
    private static readonly byte[] Tiff = [0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0, 0, 0];

    private static PolygonDto Square(double lon, double lat, double d) =>
        new("Polygon", [[[lon, lat], [lon + d, lat], [lon + d, lat + d], [lon, lat + d], [lon, lat]]]);

    private async Task<(HttpClient Client, Guid ProjectId, List<Guid> Buildings)> SetupAsync()
    {
        factory.Calc.Rules.Add("eskom/0.6.0");
        var client = await factory.EngineerClientAsync();
        var p = await (await client.PostAsJsonAsync("/api/projects", new SaveProjectRequest($"Imagery {Guid.NewGuid():N}", "eskom/0.6.0", Square(28.09, -25.53, 0.03), null)))
            .Content.ReadFromJsonAsync<ProjectDto>();
        factory.Calc.OnImport = r => new CalcImportResult(r.Kind, "geojson", "WGS84", "test",
            [.. Enumerable.Range(0, 3).Select(i => new CalcFeature($"way/{i}", Square(Lon + 0.0003 * i, Lat, 0.0001), 80, null, null, $"way/{i}", new() { ["building"] = "yes" }, []))], [], []);
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent("x"u8.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "file", "f.geojson" },
            { new StringContent("buildings"), "kind" },
        };
        (await client.PostAsync($"/api/projects/{p!.Id}/imports", form)).EnsureSuccessStatusCode();
        var fc = await client.GetFromJsonAsync<GeoFeatureCollection<BuildingProps>>($"/api/projects/{p.Id}/buildings");
        return (client, p.Id, [.. fc!.Features.Select(f => Guid.Parse(f.Id))]);
    }

    private static MultipartFormDataContent Ortho(byte[] bytes, string label, string licence, bool declaration) => new()
    {
        { new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("image/tiff") } }, "file", "ortho.tif" },
        { new StringContent(label), "label" },
        { new StringContent(licence), "licence" },
        { new StringContent(declaration ? "true" : "false"), "declaration" },
    };

    private static async Task WaitJobAsync(HttpClient client, Guid jobId)
    {
        for (var i = 0; i < 200; i++)
        {
            var j = await client.GetFromJsonAsync<JobDto>($"/api/jobs/{jobId}");
            if (j!.Status is "succeeded" or "failed") return;
            await Task.Delay(100);
        }
        throw new TimeoutException();
    }

    [Fact]
    public async Task Orthophoto_upload_records_its_licence_and_classifying_feeds_the_prediction()
    {
        var (client, projectId, buildings) = await SetupAsync();
        var bad = await client.PostAsync($"/api/projects/{projectId}/imagery/orthophoto", Ortho("not a tiff"u8.ToArray(), "", "x", false));
        var errors = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("label", out _) && errors.TryGetProperty("licence", out _) && errors.TryGetProperty("declaration", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/projects/{projectId}/imagery/orthophoto",
            Ortho("not a tiff"u8.ToArray(), "NGI 2023", "CD:NGI aerial imagery licence", true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/projects/{projectId}/imagery/classify", null)).StatusCode);

        var first = await (await client.PostAsync($"/api/projects/{projectId}/imagery/orthophoto", Ortho(Tiff, "NGI 2019", "CD:NGI aerial imagery licence", true)))
            .Content.ReadFromJsonAsync<ImageryDto>();
        var created = await client.PostAsync($"/api/projects/{projectId}/imagery/orthophoto", Ortho(Tiff, "NGI 2023 0.25 m", "CD:NGI aerial imagery licence", true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ortho = await created.Content.ReadFromJsonAsync<ImageryDto>();
        Assert.Equal(("orthophoto", "geotiff", "ready", true), (ortho!.Source, ortho.Format, ortho.Status, ortho.Active));
        var index = await client.GetFromJsonAsync<ImageryIndex>($"/api/projects/{projectId}/imagery");
        Assert.False(index!.Items.Single(i => i.Id == first!.Id).Active);
        Assert.False(index.Google.Available);

        // Confirm one building on site; the other two are predicted.
        var b0 = await client.GetFromJsonAsync<BuildingFieldDto>($"/api/projects/{projectId}/buildings/{buildings[0]}");
        (await client.PutAsJsonAsync($"/api/projects/{projectId}/buildings/{buildings[0]}/inspection",
            new BuildingInspectionRequest(Guid.NewGuid(), "correct", "house", null, DateTimeOffset.UtcNow, null, b0!.Version))).EnsureSuccessStatusCode();

        var started = await (await client.PostAsync($"/api/projects/{projectId}/imagery/classify", null)).Content.ReadFromJsonAsync<StartedImagery>();
        Assert.Equal("imagery.classify", started!.Job.Kind);
        await WaitJobAsync(client, started.Job.Id);
        var sent = factory.Calc.LastRooftop!.Value;
        Assert.Equal(("geotiff", "NGI 2023 0.25 m", "eskom/0.6.0"), (sent.GetProperty("imagery_kind").GetString(), sent.GetProperty("imagery_label").GetString(), sent.GetProperty("rules").GetString()));
        Assert.Equal(Tiff.Length, factory.Calc.LastRooftopBytes);
        var sentBuildings = sent.GetProperty("buildings").EnumerateArray().ToList();
        Assert.Equal(3, sentBuildings.Count);
        Assert.Equal("house", sentBuildings.Single(b => b.GetProperty("id").GetString() == buildings[0].ToString()).GetProperty("confirmed_type").GetString());
        Assert.Equal(5, sentBuildings[0].GetProperty("footprint")[0].GetArrayLength());

        var fc = await client.GetFromJsonAsync<GeoFeatureCollection<BuildingProps>>($"/api/projects/{projectId}/buildings");
        var predicted = fc!.Features.Where(f => f.Id != buildings[0].ToString()).ToList();
        Assert.All(predicted, f => Assert.Equal(("shop", "rooftop:NGI 2023 0.25 m"), (f.Properties.PredictedType, f.Properties.Source)));
        Assert.Equal("house", fc.Features.Single(f => f.Id == buildings[0].ToString()).Properties.EffectiveType);
        index = await client.GetFromJsonAsync<ImageryIndex>($"/api/projects/{projectId}/imagery");
        var model = index!.Items.Single(i => i.Active).Model!.Value;
        Assert.True(model.GetProperty("model").GetProperty("used").GetBoolean());
        Assert.Equal(2, model.GetProperty("signals").GetInt32());

        // A model that is not good enough takes its signals away again.
        factory.Calc.RooftopModelUsed = false;
        try
        {
            started = await (await client.PostAsync($"/api/projects/{projectId}/imagery/classify", null)).Content.ReadFromJsonAsync<StartedImagery>();
            await WaitJobAsync(client, started!.Job.Id);
            fc = await client.GetFromJsonAsync<GeoFeatureCollection<BuildingProps>>($"/api/projects/{projectId}/buildings");
            Assert.All(fc!.Features.Where(f => f.Id != buildings[0].ToString()), f => Assert.DoesNotContain("rooftop", f.Properties.Source));
        }
        finally
        {
            factory.Calc.RooftopModelUsed = true;
        }
    }

    [Fact]
    public async Task Google_imagery_needs_the_licence_setting_then_fetches_tiles_over_the_buildings()
    {
        var (client, projectId, _) = await SetupAsync();
        var off = await client.PostAsync($"/api/projects/{projectId}/imagery/google", null);
        Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode);
        Assert.Contains("agreement that allows deriving data", await off.Content.ReadAsStringAsync());

        factory.GoogleOptions.Enabled = true;
        factory.GoogleOptions.ApiKey = "test-key";
        factory.GoogleOptions.LicenceReference = "Google Maps Platform agreement GMP-123 (derived use allowed)";
        try
        {
            Assert.True((await client.GetFromJsonAsync<ImageryIndex>($"/api/projects/{projectId}/imagery"))!.Google.Available);
            var r = await client.PostAsync($"/api/projects/{projectId}/imagery/google", null);
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
            var started = await r.Content.ReadFromJsonAsync<StartedImagery>();
            Assert.Equal(("google", "tiles", "fetching"), (started!.Imagery.Source, started.Imagery.Format, started.Imagery.Status));
            await WaitJobAsync(client, started.Job.Id);
            var imagery = (await client.GetFromJsonAsync<ImageryIndex>($"/api/projects/{projectId}/imagery"))!.Items.Single(i => i.Id == started.Imagery.Id);
            Assert.True(imagery.Status == "ready", imagery.Error);
            Assert.Equal("Google Maps Platform agreement GMP-123 (derived use allowed)", imagery.Licence);
            var tileRequests = factory.GoogleTiles.Requests.Where(u => u.AbsolutePath.Contains("/2dtiles/19/")).ToList();
            Assert.InRange(tileRequests.Count, 1, 400);
            Assert.All(tileRequests, u => Assert.Contains("session=sess-1", u.Query));

            await client.PostAsync($"/api/projects/{projectId}/imagery/classify", null);
            for (var i = 0; i < 100 && factory.Calc.LastRooftop?.GetProperty("imagery_kind").GetString() != "tiles"; i++) await Task.Delay(100);
            Assert.Equal("tiles", factory.Calc.LastRooftop!.Value.GetProperty("imagery_kind").GetString());
            Assert.True(factory.Calc.LastRooftopBytes > 0);

            factory.GoogleOptions.MaxTiles = 1;
            var tooMany = await (await client.PostAsync($"/api/projects/{projectId}/imagery/google", null)).Content.ReadFromJsonAsync<StartedImagery>();
            await WaitJobAsync(client, tooMany!.Job.Id);
            var failed = (await client.GetFromJsonAsync<ImageryIndex>($"/api/projects/{projectId}/imagery"))!.Items.Single(i => i.Id == tooMany.Imagery.Id);
            Assert.Equal("failed", failed.Status);
            Assert.Contains("the limit is 1", failed.Error);
        }
        finally
        {
            factory.GoogleOptions.Enabled = false;
            factory.GoogleOptions.ApiKey = null;
            factory.GoogleOptions.LicenceReference = null;
            factory.GoogleOptions.MaxTiles = 400;
        }
    }
}
