using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Reticula.Api.Layout;
using Reticula.Api.Projects;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Geo;
using static Reticula.Api.Tests.TestFixtures;

namespace Reticula.Api.Tests;

/// <summary>Roads, contours and the authority's existing network, from files or OpenStreetMap.</summary>
[Collection(ApiCollection.Name)]
public class LayoutLinesTests(ReticulaApiFactory factory)
{
    private const double Lon = 28.10, Lat = -25.52;

    private static GeoJsonGeometry Street(double dy = 0) => GeoJsonGeometry.Line([Lon, Lat + dy], [Lon + 0.002, Lat + dy]);

    private static CalcImportResult Result(string kind, params CalcFeature[] features) => new(kind, "geojson", "WGS84", "test", features, [], []);

    private Task<(HttpClient Client, Guid ProjectId)> NewProjectAsync() => factory.NewProjectAsync("Lines", Square(28.09, -25.53, 0.03));

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid projectId, string kind, string? source = null)
    {
        var form = new MultipartFormDataContent { { new StringContent(kind), "kind" } };
        if (source is not null) form.Add(new StringContent(source), "source");
        else form.Add(new ByteArrayContent("{}"u8.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }, "file", "x.geojson");
        return client.PostAsync($"/api/projects/{projectId}/imports", form);
    }

    [Fact]
    public async Task Roads_import_replaces_the_previous_roads()
    {
        var (client, projectId) = await NewProjectAsync();
        factory.Calc.OnImport = r => Result(r.Kind,
            new CalcFeature("w1", Street(), 0, null, null, "way/1", new() { ["highway"] = "residential" }, [], 201, "Mmabatho St", "residential"),
            new CalcFeature("w2", Street(0.001), 0, null, null, null, [], [], 201));
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, projectId, "roads")).StatusCode);

        var roads = await client.GetFromJsonAsync<GeoFeatureCollection<RoadProps>>($"/api/projects/{projectId}/roads");
        Assert.Equal(2, roads!.Features.Count);
        var named = roads.Features.Single(f => f.Properties.Name == "Mmabatho St");
        Assert.Equal(("residential", 201.0, "way/1"), (named.Properties.RoadClass, named.Properties.LengthM, named.Properties.OsmId));
        Assert.Equal("LineString", ((JsonElement)named.Geometry).GetProperty("type").GetString());

        factory.Calc.OnImport = r => Result(r.Kind, new CalcFeature("w9", Street(0.002), 0, null, null, null, [], [], 201, "Ruth First Ave"));
        await UploadAsync(client, projectId, "roads");
        roads = await client.GetFromJsonAsync<GeoFeatureCollection<RoadProps>>($"/api/projects/{projectId}/roads");
        Assert.Equal(["Ruth First Ave"], roads!.Features.Select(f => f.Properties.Name));
    }

    [Fact]
    public async Task Contours_keep_their_elevation()
    {
        var (client, projectId) = await NewProjectAsync();
        factory.Calc.OnImport = r => Result(r.Kind,
            new CalcFeature("c1", Street(), 0, null, null, null, [], [], 201, ElevationM: 1255),
            new CalcFeature("c2", Street(0.001), 0, null, null, null, [], [], 201, ElevationM: 1250));
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, projectId, "contours")).StatusCode);
        var contours = await client.GetFromJsonAsync<GeoFeatureCollection<ContourProps>>($"/api/projects/{projectId}/contours");
        Assert.Equal([1250.0, 1255.0], contours!.Features.Select(f => f.Properties.ElevationM));
    }

    [Fact]
    public async Task Network_assets_keep_their_type_ratings_and_missing_fields()
    {
        var (client, projectId) = await NewProjectAsync();
        factory.Calc.OnImport = r => Result(r.Kind,
            new CalcFeature("t1", GeoJsonGeometry.Point(Lon, Lat), 0, null, null, null, [], [], Name: "TRF 12", Category: "transformer", RatingKva: 100),
            new CalcFeature("l1", Street(), 0, null, null, null, [], [], 201, Category: "mv_line", VoltageKv: 11),
            new CalcFeature("p1", GeoJsonGeometry.Point(Lon + 0.001, Lat), 0, null, null, null, [], [], Category: "connection_point", VoltageKv: 11,
                Missing: ["capacity_kva", "fault_level_ka"]));
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, projectId, "network")).StatusCode);

        var net = await client.GetFromJsonAsync<GeoFeatureCollection<NetworkProps>>($"/api/projects/{projectId}/network");
        var byType = net!.Features.ToDictionary(f => f.Properties.AssetType, f => f.Properties);
        Assert.Equal(("TRF 12", 100.0), (byType["transformer"].Label, byType["transformer"].RatingKva!.Value));
        Assert.Equal(11, byType["mv_line"].VoltageKv);
        Assert.Equal(["capacity_kva", "fault_level_ka"], byType["connection_point"].Missing);

        var summary = await client.GetFromJsonAsync<LayoutSummary>($"/api/projects/{projectId}/layout-summary");
        Assert.Equal((3, 1), (summary!.NetworkAssets, summary.NetworkIncomplete));
    }

    [Fact]
    public async Task Buildings_and_roads_can_come_from_openstreetmap_without_a_file()
    {
        var (client, projectId) = await NewProjectAsync();
        factory.Calc.OnImportOsm = kind => new CalcImportResult(kind, "overpass", "WGS84", "OSM", kind == "roads"
            ? [new CalcFeature("way/5", Street(), 0, null, null, "way/5", new() { ["highway"] = "tertiary" }, [], 201, "Ruth First Ave", "tertiary")]
            : [new CalcFeature("way/6", Square(Lon, Lat, 0.0001), 100, null, null, "way/6", new() { ["building"] = "house" }, [])], [], []);

        var r = await UploadAsync(client, projectId, "roads", source: "osm");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, projectId, "buildings", source: "osm")).StatusCode);

        var summary = await client.GetFromJsonAsync<LayoutSummary>($"/api/projects/{projectId}/layout-summary");
        Assert.Equal((1, 1), (summary!.Roads, summary.Buildings));
        var batches = await client.GetFromJsonAsync<List<ImportBatchDto>>($"/api/projects/{projectId}/imports");
        Assert.All(batches!, b => Assert.Equal("OpenStreetMap (Overpass)", b.FileName));

        // Only buildings and roads come from OpenStreetMap; a file import still needs a file.
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, projectId, "network", source: "osm")).StatusCode);
        var noFile = new MultipartFormDataContent { { new StringContent("roads"), "kind" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/projects/{projectId}/imports", noFile)).StatusCode);
    }

    [Fact]
    public async Task An_unreachable_openstreetmap_is_reported()
    {
        var (client, projectId) = await NewProjectAsync();
        factory.Calc.OnImportOsm = _ => throw new CalcRejectedException("OpenStreetMap (Overpass) could not be reached: timed out");
        try
        {
            var r = await UploadAsync(client, projectId, "roads", source: "osm");
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("could not be reached", await r.Content.ReadAsStringAsync());
        }
        finally
        {
            factory.Calc.OnImportOsm = kind => new CalcImportResult(kind, "overpass", "WGS84", "OSM", [], [], []);
        }
    }
}
