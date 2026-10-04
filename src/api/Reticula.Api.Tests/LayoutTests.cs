using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Reticula.Api.Auth;
using Reticula.Api.Layout;
using Reticula.Api.Projects;
using Reticula.Domain.Auth;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Geo;

namespace Reticula.Api.Tests;

[Collection(ApiCollection.Name)]
public class LayoutTests(ReticulaApiFactory factory)
{
    private const double Lon = 28.10, Lat = -25.52;

    private static PolygonDto Square(double lon, double lat, double d) =>
        new("Polygon", [[[lon, lat], [lon + d, lat], [lon + d, lat + d], [lon, lat + d], [lon, lat]]]);

    private static CalcFeature Stand(string erf, double lon, string? zoning = "Residential 1") =>
        new($"pm-{erf}", Square(lon, Lat, 0.0003), 1000, erf, zoning, null, [], []);

    private static CalcFeature Building(string id, double lon, Dictionary<string, string>? tags = null) =>
        new(id, Square(lon + 0.0001, Lat + 0.0001, 0.0001), 100, null, null, id, tags ?? new() { ["building"] = "yes" }, []);

    private static CalcImportResult Result(string kind, IReadOnlyList<CalcFeature> features, params CalcIssue[] issues) =>
        new(kind, "kml", "WGS84", "test", features, issues, []);

    private async Task<(HttpClient Client, Guid ProjectId)> NewProjectAsync()
    {
        var client = await factory.EngineerClientAsync();
        var r = await client.PostAsJsonAsync("/api/projects", new SaveProjectRequest($"Layout {Guid.NewGuid():N}", "eskom/0.1.0", Square(28.09, -25.53, 0.03), null));
        r.EnsureSuccessStatusCode();
        return (client, (await r.Content.ReadFromJsonAsync<ProjectDto>())!.Id);
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid projectId, string kind, bool dryRun = false, string fileName = "layout.kml")
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent("<kml/>"u8.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "file", fileName },
            { new StringContent(kind), "kind" },
            { new StringContent(dryRun ? "true" : "false"), "dryRun" },
        };
        return client.PostAsync($"/api/projects/{projectId}/imports", form);
    }

    [Fact]
    public async Task Dry_run_previews_without_storing()
    {
        var (client, projectId) = await NewProjectAsync();
        factory.Calc.OnImport = r => Result(r.Kind, [Stand("1001", Lon), Stand("1002", Lon + 0.0003)]);

        var r = await UploadAsync(client, projectId, "stands", dryRun: true);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<ImportResponse>();
        Assert.False(body!.Committed);
        Assert.Equal(2, body.Preview!.Features.Count);
        Assert.Contains("\"type\":\"Polygon\"", factory.Calc.LastImport!.AreaGeoJson); // project area sent for checks

        var stands = await client.GetFromJsonAsync<GeoFeatureCollection<StandProps>>($"/api/projects/{projectId}/stands");
        Assert.Empty(stands!.Features);
    }

    [Fact]
    public async Task Stands_then_buildings_link_and_predict_with_zoning()
    {
        var (client, projectId) = await NewProjectAsync();
        factory.Calc.OnImport = r => Result(r.Kind, [Stand("1001", Lon), Stand("1002", Lon + 0.0003, zoning: null)]);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, projectId, "stands")).StatusCode);

        factory.Calc.OnImport = r => Result(r.Kind, [
            Building("way/1", Lon),                                                  // on stand 1001 (residential)
            Building("way/2", Lon + 0.0003, new() { ["building"] = "house" }),       // on stand 1002, tagged house
            Building("way/3", Lon + 0.01),                                           // on no stand
        ]);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, projectId, "buildings")).StatusCode);

        Assert.Contains(factory.Calc.LastPredictionInputs, i => i.Zoning == "Residential 1");
        var fc = await client.GetFromJsonAsync<GeoFeatureCollection<BuildingProps>>($"/api/projects/{projectId}/buildings");
        var byErf = fc!.Features.ToDictionary(f => f.Properties.Erf ?? "none");
        Assert.Equal(("house", "zoning:Residential 1"), (byErf["1001"].Properties.PredictedType, byErf["1001"].Properties.Source));
        Assert.Equal(("house", 0.9), (byErf["1002"].Properties.PredictedType, byErf["1002"].Properties.Confidence));
        Assert.True(byErf["none"].Properties.LowConfidence);
        Assert.True(fc.Features[0].Properties.LowConfidence, "low-confidence buildings are listed first");

        var summary = await client.GetFromJsonAsync<LayoutSummary>($"/api/projects/{projectId}/layout-summary");
        Assert.Equal((2, 3, 1), (summary!.Stands, summary.Buildings, summary.LowConfidence));
        Assert.Equal(2, summary.PredictedByType["house"]);
    }

    [Fact]
    public async Task Reimporting_stands_replaces_them_and_relinks_buildings()
    {
        var (client, projectId) = await NewProjectAsync();
        factory.Calc.OnImport = r => Result(r.Kind, [Stand("1001", Lon)]);
        await UploadAsync(client, projectId, "stands");
        factory.Calc.OnImport = r => Result(r.Kind, [Building("way/1", Lon)]);
        await UploadAsync(client, projectId, "buildings");

        factory.Calc.OnImport = r => Result(r.Kind, [Stand("2001", Lon), Stand("2002", Lon + 0.0003)]);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(client, projectId, "stands")).StatusCode);

        var stands = await client.GetFromJsonAsync<GeoFeatureCollection<StandProps>>($"/api/projects/{projectId}/stands");
        Assert.Equal(["2001", "2002"], stands!.Features.Select(f => f.Properties.Erf));
        var buildings = await client.GetFromJsonAsync<GeoFeatureCollection<BuildingProps>>($"/api/projects/{projectId}/buildings");
        Assert.Equal("2001", buildings!.Features.Single().Properties.Erf);

        var imports = await client.GetFromJsonAsync<List<ImportBatchDto>>($"/api/projects/{projectId}/imports");
        Assert.Equal(3, imports!.Count);
    }

    [Fact]
    public async Task File_with_errors_is_not_stored()
    {
        var (client, projectId) = await NewProjectAsync();
        factory.Calc.OnImport = r => Result(r.Kind, [Stand("1", 2.35)],
            new CalcIssue("error", "outside_area", "No features fall inside the project area.", 1, ["pm-1"]));

        var r = await UploadAsync(client, projectId, "stands");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<ImportResponse>();
        Assert.Equal("outside_area", body!.Issues.Single().Code);
        Assert.Empty((await client.GetFromJsonAsync<List<ImportBatchDto>>($"/api/projects/{projectId}/imports"))!);
    }

    [Fact]
    public async Task Unreadable_file_returns_400_with_the_reason()
    {
        var (client, projectId) = await NewProjectAsync();
        factory.Calc.OnImport = _ => throw new CalcRejectedException("Unsupported file type for layout.dwg");
        var r = await UploadAsync(client, projectId, "stands", fileName: "layout.dwg");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Unsupported file type", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_kind_is_rejected()
    {
        var (client, projectId) = await NewProjectAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, projectId, "roads")).StatusCode);
    }

    [Fact]
    public async Task Inspector_reads_layout_but_cannot_import()
    {
        var (engineer, projectId) = await NewProjectAsync();
        var email = $"inspector-{Guid.NewGuid():N}@test.local";
        (await engineer.PostAsJsonAsync("/api/users", new CreateUserRequest(email, "Inspector", "inspector-pass-1", Roles.Inspector, null))).EnsureSuccessStatusCode();
        var inspector = await factory.ClientAsAsync(email, "inspector-pass-1");

        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(inspector, projectId, "stands")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await inspector.GetAsync($"/api/projects/{projectId}/buildings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.PostAsync($"/api/projects/{projectId}/predictions", null)).StatusCode);
    }
}
