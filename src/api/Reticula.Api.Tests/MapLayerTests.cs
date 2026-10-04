using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Reticula.Api.Layout;
using Reticula.Api.Projects;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Geo;

namespace Reticula.Api.Tests;

/// <summary>Roads, contours and existing network imports, and fetching OpenStreetMap data (plan item 1.1).</summary>
[Collection(ApiCollection.Name)]
public class MapLayerTests(ReticulaApiFactory factory)
{
    private const double Lon = 28.10, Lat = -25.52;

    private static PolygonDto Square(double lon, double lat, double d) =>
        new("Polygon", [[[lon, lat], [lon + d, lat], [lon + d, lat + d], [lon, lat + d], [lon, lat]]]);

    private static GeometryInput Line(params double[][] pts) => new("LineString", JsonSerializer.SerializeToElement(pts));
    private static GeometryInput Pt(double lon, double lat) => new("Point", JsonSerializer.SerializeToElement(new[] { lon, lat }));

    private static CalcFeature Road(string id, string cls, double dy = 0) =>
        new(id, Line([Lon, Lat + dy], [Lon + 0.002, Lat + dy]), 0, null, null, id, [], [], 200, $"Street {id}", cls);

    private static CalcImportResult Result(string kind, IReadOnlyList<CalcFeature> features, params CalcIssue[] issues) =>
        new(kind, "geojson", "WGS84", "test", features, issues, []);

    private async Task<(HttpClient Client, Guid ProjectId)> NewProjectAsync()
    {
        var client = await factory.EngineerClientAsync();
        var r = await client.PostAsJsonAsync("/api/projects", new SaveProjectRequest($"Layers {Guid.NewGuid():N}", "eskom/0.1.0", Square(28.09, -25.53, 0.03), null));
        r.EnsureSuccessStatusCode();
        return (client, (await r.Content.ReadFromJsonAsync<ProjectDto>())!.Id);
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid projectId, string kind, bool dryRun = false, string? contourInterval = null)
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent("{}"u8.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "file", "layer.geojson" },
            { new StringContent(kind), "kind" },
            { new StringContent(dryRun ? "true" : "false"), "dryRun" },
        };
        if (contourInterval is not null) form.Add(new StringContent(contourInterval), "contourInterval");
        return client.PostAsync($"/api/projects/{projectId}/imports", form);
    }

    [Fact]
    public async Task Roads_are_stored_as_a_layer_and_replaced_on_reimport()
    {
        var (client, projectId) = await NewProjectAsync();
        factory.Calc.OnImport = r => Result(r.Kind, [Road("way/1", "residential"), Road("way/2", "tertiary", 0.001)]);

        var preview = await (await UploadAsync(client, projectId, "roads", dryRun: true)).Content.ReadFromJsonAsync<ImportResponse>();
        Assert.Equal(("residential", "Street way/1", 200.0), (preview!.Preview!.Features[0].Properties.Subtype, preview.Preview.Features[0].Properties.Name, preview.Preview.Features[0].Properties.LengthM));
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, projectId, "roads")).StatusCode);

        var roads = await client.GetFromJsonAsync<GeoFeatureCollection<MapFeatureProps>>($"/api/projects/{projectId}/map-features?layer=roads");
        Assert.Equal(2, roads!.Features.Count);
        Assert.Contains("LineString", JsonSerializer.Serialize(roads.Features[0].Geometry));

        factory.Calc.OnImport = r => Result(r.Kind, [Road("way/9", "primary")]);
        await UploadAsync(client, projectId, "roads");
        roads = await client.GetFromJsonAsync<GeoFeatureCollection<MapFeatureProps>>($"/api/projects/{projectId}/map-features?layer=roads");
        Assert.Equal(["primary"], roads!.Features.Select(f => f.Properties.Subtype));

        var summary = await client.GetFromJsonAsync<LayoutSummary>($"/api/projects/{projectId}/layout-summary");
        Assert.Equal((1, 0, 0), (summary!.Roads, summary.Contours, summary.NetworkAssets));
        var imports = await client.GetFromJsonAsync<List<ImportBatchDto>>($"/api/projects/{projectId}/imports");
        Assert.Equal(["roads", "roads"], imports!.Select(i => i.Kind));
    }

    [Fact]
    public async Task Contours_keep_their_elevation_and_the_interval_reaches_the_calc_service()
    {
        var (client, projectId) = await NewProjectAsync();
        factory.Calc.OnImport = r => Result(r.Kind, [
            new("c1", Line([Lon, Lat], [Lon + 0.002, Lat]), 0, null, null, null, [], [], 210, null, null, 1302),
            new("c2", Line([Lon, Lat + 0.001], [Lon + 0.002, Lat + 0.001]), 0, null, null, null, [], [], 210, null, null, 1304)]);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, projectId, "contours", contourInterval: "2")).StatusCode);
        Assert.Equal(2.0, factory.Calc.LastImport!.ContourInterval);

        var contours = await client.GetFromJsonAsync<GeoFeatureCollection<MapFeatureProps>>($"/api/projects/{projectId}/map-features?layer=contours");
        Assert.Equal([1302.0, 1304.0], contours!.Features.Select(f => f.Properties.ElevationM!.Value));

        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, projectId, "contours", contourInterval: "0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/projects/{projectId}/map-features?layer=rivers")).StatusCode);
    }

    [Fact]
    public async Task Network_assets_keep_their_fields_and_a_file_with_errors_stores_nothing()
    {
        var (client, projectId) = await NewProjectAsync();
        var attrs = new Dictionary<string, JsonElement> { ["asset_type"] = JsonSerializer.SerializeToElement("transformer"), ["rating_kva"] = JsonSerializer.SerializeToElement(200.0) };
        factory.Calc.OnImport = r => Result(r.Kind, [new("T1", Pt(Lon, Lat), 0, null, null, null, [], attrs, 0, "T1", "transformer")],
            new CalcIssue("error", "network_field_missing", "Some assets are missing voltage_kv.", 1, ["T1"]));
        var bad = await UploadAsync(client, projectId, "network");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<GeoFeatureCollection<MapFeatureProps>>($"/api/projects/{projectId}/map-features?layer=network"))!.Features);

        factory.Calc.OnImport = r => Result(r.Kind, [new("T1", Pt(Lon, Lat), 0, null, null, null, [], attrs, 0, "T1", "transformer")]);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, projectId, "network")).StatusCode);
        var net = await client.GetFromJsonAsync<GeoFeatureCollection<MapFeatureProps>>($"/api/projects/{projectId}/map-features");
        var t = Assert.Single(net!.Features);
        Assert.Equal(("network", "transformer", 200.0), (t.Properties.Layer, t.Properties.Subtype, t.Properties.Attributes.GetProperty("rating_kva").GetDouble()));
    }

    [Fact]
    public async Task Roads_and_buildings_can_be_fetched_from_openstreetmap_for_the_project_area()
    {
        var (client, projectId) = await NewProjectAsync();
        factory.Overpass.Body = "{\"elements\":[{\"type\":\"way\",\"id\":1}]}";
        factory.Calc.OnImport = r => Result(r.Kind, [Road("way/1", "residential")]);

        var r = await client.PostAsJsonAsync($"/api/projects/{projectId}/imports/overpass", new OverpassImportRequest("roads", true));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("way[\"highway\"](-25.531,28.089,-25.499,28.121);", factory.Overpass.LastQuery);
        Assert.Equal("overpass-roads.json", factory.Calc.LastImport!.FileName);

        r = await client.PostAsJsonAsync($"/api/projects/{projectId}/imports/overpass", new OverpassImportRequest("roads", false));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);

        await client.PostAsJsonAsync($"/api/projects/{projectId}/imports/overpass", new OverpassImportRequest("buildings", true));
        Assert.Contains("relation[\"building\"]", factory.Overpass.LastQuery);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/projects/{projectId}/imports/overpass", new OverpassImportRequest("stands", true))).StatusCode);

        factory.Overpass.Status = HttpStatusCode.TooManyRequests;
        try
        {
            var down = await client.PostAsJsonAsync($"/api/projects/{projectId}/imports/overpass", new OverpassImportRequest("roads", true));
            Assert.Equal(HttpStatusCode.BadGateway, down.StatusCode);
            Assert.Contains("429", await down.Content.ReadAsStringAsync());
        }
        finally
        {
            factory.Overpass.Status = HttpStatusCode.OK;
        }
    }
}
